using System.Collections.Frozen;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Services;

public sealed record StripRequest
{
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>输出位置；为 <c>null</c> 时就地替换原文件。</summary>
    public OutputOptions? Output { get; init; }

    public bool ConvertToHeic { get; init; } = true;

    public int HeicQuality { get; init; } = ConversionDefaults.HeicQuality;

    public int Parallelism { get; init; } = ConversionDefaults.Parallelism;
}

/// <summary>
/// 一张待瘦身照片的分析结果。
/// </summary>
/// <param name="ImagePath">照片路径</param>
/// <param name="ImageBytes">照片文件大小</param>
/// <param name="EmbeddedVideo">内嵌视频</param>
/// <param name="CompanionVideo">经配对校验确认属于该照片的同名视频（Apple 实况照片）</param>
/// <param name="CompanionBytes">同名视频大小</param>
/// <param name="HasGainMap">带 Ultra HDR 增益图时不转码 HEIC，以免丢失 HDR</param>
public sealed record StripCandidate(string ImagePath, long ImageBytes, EmbeddedVideo? EmbeddedVideo, string? CompanionVideo, long CompanionBytes, bool HasGainMap)
{
    /// <summary>HEIC 质量 90 时相对 JPEG 的典型体积比例；没有实测压缩比时用于预估。</summary>
    public const double DefaultHeicSizeRatio = 0.45;

    /// <summary>分析失败的原因（文件消失、无法读取等）；不为 <c>null</c> 时该文件不做任何处理。</summary>
    public string? AnalysisError { get; init; }

    public long OriginalBytes => ImageBytes + CompanionBytes;

    /// <summary>剥离后减少的字节：截断点之后的全部内容（含视频外层的容器头）加上配对视频。</summary>
    public long VideoBytes => (EmbeddedVideo is { } video ? ImageBytes - video.ImageEnd : 0) + CompanionBytes;

    public bool HasVideo => EmbeddedVideo is not null || CompanionVideo is not null;

    public bool WillConvert(bool convertToHeic) => AnalysisError is null && convertToHeic && !HasGainMap && !MediaFileTypes.IsHeic(ImagePath);

    /// <summary>剥离视频后的照片字节数（转码前）。</summary>
    public long PhotoBytes => EmbeddedVideo?.ImageEnd ?? ImageBytes;

    public long EstimateFinalBytes(bool convertToHeic) => EstimateFinalBytes(convertToHeic, DefaultHeicSizeRatio);

    /// <param name="convertToHeic">是否转码 HEIC</param>
    /// <param name="heicSizeRatio">HEIC 相对转码前照片的体积比例（例如抽样实测值）</param>
    public long EstimateFinalBytes(bool convertToHeic, double heicSizeRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heicSizeRatio);
        return WillConvert(convertToHeic) ? (long)(PhotoBytes * heicSizeRatio) : PhotoBytes;
    }

    internal static StripCandidate Unavailable(string imagePath, string error) =>
        new(imagePath, 0, null, null, 0, false) { AnalysisError = error };
}

/// <summary>
/// 空间瘦身：剥离动态照片的内嵌视频或 Apple 实况照片的配对视频，并可选转码为 HEIC。
/// </summary>
public sealed class MotionPhotoStripper(IMetadataService metadata, IImageConverter imageConverter)
{
    private static readonly FrozenSet<string> CompanionExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".mov", ".mp4");

    /// <summary>
    /// 列出目录中（不含子目录）可能需要瘦身的照片。
    /// </summary>
    public static IReadOnlyList<string> FindCandidates(string directory) =>
    [
        .. Directory.EnumerateFiles(directory, "*", new EnumerationOptions { IgnoreInaccessible = true })
                    .Where(path => MediaFileTypes.MotionPhotoExtensions.Contains(Path.GetExtension(path)))
                    .Order(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// 只读分析：定位内嵌视频、校验同名视频是否确属同一张实况照片。
    /// 单个文件分析失败只把该文件标为不可瘦身（见 <see cref="StripCandidate.AnalysisError"/>），不影响其它文件。
    /// </summary>
    /// <remarks>目录枚举与文件读取都是同步 IO，整体放到线程池执行，调用方（界面线程）不会被阻塞。</remarks>
    public Task<IReadOnlyList<StripCandidate>> AnalyzeAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => AnalyzeCoreAsync(files, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<StripCandidate>> AnalyzeCoreAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var companions = FindCompanionVideos(files);
        var tags = await ReadCompanionTagsAsync(companions, cancellationToken);

        var results = new StripCandidate[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ConversionDefaults.Parallelism, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await AnalyzeOneAsync(files[index], companions, tags, token));

        return results;
    }

    private async Task<StripCandidate> AnalyzeOneAsync(
        string file,
        Dictionary<string, string> companions,
        IReadOnlyDictionary<string, MediaMetadata>? tags,
        CancellationToken cancellationToken)
    {
        try
        {
            var layout = await ImageInspector.InspectAsync(file, metadata, cancellationToken);
            var imageBytes = new FileInfo(file).Length;
            string? companion = null;
            long companionBytes = 0;
            if (layout.Video is null
                && companions.TryGetValue(file, out var video)
                && tags is not null
                && tags.TryGetValue(file, out var photoTags)
                && tags.TryGetValue(video, out var videoTags)
                && PairValidator.Validate(photoTags, videoTags).IsAccepted
                && TryGetLength(video) is { } length)
            {
                companion = video;
                companionBytes = length;
            }

            return new StripCandidate(file, imageBytes, layout.Video, companion, companionBytes, layout.HasGainMap);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return StripCandidate.Unavailable(file, ex.Message);
        }
    }

    /// <summary>
    /// 批量读取照片与同名视频的元数据；读取失败时无法校验配对，返回 <c>null</c>，同名视频一律不动。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MediaMetadata>?> ReadCompanionTagsAsync(Dictionary<string, string> companions, CancellationToken cancellationToken)
    {
        if (companions.Count == 0)
        {
            return null;
        }

        try
        {
            return await metadata.ReadAsync([.. companions.Keys, .. companions.Values], MetadataScope.Standard, cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    public async Task<BatchReport> StripAsync(StripRequest request, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files.Count == 0)
        {
            return BatchReport.Empty;
        }

        var candidates = await AnalyzeAsync(request.Files, cancellationToken);
        var protectedPaths = candidates.SelectMany(candidate => candidate.CompanionVideo is null ? [candidate.ImagePath] : (string[])[candidate.ImagePath, candidate.CompanionVideo]);
        var committer = new OutputCommitter(request.Output?.Conflict ?? ConflictPolicy.AppendIndex, protectedPaths);
        var companionDisposition = new SourceDisposition(SourceFileAction.Recycle, string.Empty);
        if (request.Output is not null)
        {
            OutputCommitter.DeleteStaleStagingFiles(request.Output.Directory, ConversionDefaults.StaleStagingAge);
        }

        using var workspace = new TempWorkspace();
        return await BatchRunner.RunAsync(
            candidates,
            candidate => candidate.ImagePath,
            (candidate, token) => StripOneAsync(candidate, request, committer, companionDisposition, workspace, token),
            request.Parallelism,
            progress,
            cancellationToken: cancellationToken);
    }

    private async Task<ItemOutcome> StripOneAsync(
        StripCandidate candidate,
        StripRequest request,
        OutputCommitter committer,
        SourceDisposition companionDisposition,
        TempWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var source = candidate.ImagePath;
        if (candidate.AnalysisError is { } analysisError)
        {
            return ItemOutcome.Failed(source, analysisError);
        }

        var convert = candidate.WillConvert(request.ConvertToHeic);
        if (!candidate.HasVideo && !convert)
        {
            return ItemOutcome.Skipped(source, candidate.HasGainMap ? "带 HDR 增益图且无内嵌视频，为保留 HDR 不转码" : "无内嵌视频，且无需转换格式");
        }

        if (candidate.ImageBytes == 0)
        {
            throw new InvalidDataException("文件为空。");
        }

        var inPlace = request.Output is null;
        var directory = inPlace ? Path.GetDirectoryName(Path.GetFullPath(source))! : request.Output!.DirectoryFor(source);
        var extension = convert ? ".heic" : Path.GetExtension(source);
        var timestamp = FileTimestamp.Read(source);
        var photoChanged = candidate.EmbeddedVideo is not null || convert;

        string? clean = null;
        string? staged = null;
        try
        {
            var photo = source;
            if (candidate.EmbeddedVideo is { } embedded)
            {
                clean = workspace.NewFile(Path.GetExtension(source));
                await BinaryFile.CopySegmentAsync(source, clean, 0, embedded.ImageEnd, cancellationToken);
                await metadata.RemoveMotionPhotoAsync(clean, cancellationToken);
                photo = clean;
            }

            string final;
            if (inPlace && !photoChanged)
            {
                final = source;
            }
            else
            {
                staged = OutputCommitter.CreateStagingPath(directory, extension);
                if (convert)
                {
                    await imageConverter.ConvertToHeicAsync(photo, staged, request.HeicQuality, cancellationToken);
                }
                else
                {
                    File.Copy(photo, staged);
                }

                EnsureValidImage(staged);
                final = inPlace
                    ? committer.ReplaceSource(staged, source, extension, timestamp)
                    : committer.Commit(staged, directory, Path.GetFileNameWithoutExtension(source) + extension, timestamp);
            }

            var companionError = inPlace && candidate.CompanionVideo is { } companion ? companionDisposition.Apply(companion) : null;
            var removedCompanion = candidate.CompanionVideo is not null && inPlace && companionError is null;
            var before = candidate.ImageBytes + (removedCompanion || !inPlace ? candidate.CompanionBytes : 0);
            return ItemOutcome.Succeeded(source, final) with
            {
                BytesSaved = Math.Max(0, before - new FileInfo(final).Length),
                CleanupError = companionError
            };
        }
        catch
        {
            FileHelper.TryDeleteFile(staged);
            throw;
        }
        finally
        {
            FileHelper.TryDeleteFile(clean);
        }
    }

    /// <summary>
    /// 为每张照片找同目录下的同名视频（IMG_0001.HEIC 对应 IMG_0001.MOV）；是否真的属于该照片需要再做配对校验。
    /// </summary>
    /// <remarks>
    /// 每个目录只枚举一次并建立「主干 → 磁盘上的真实文件名」索引（忽略大小写），
    /// 逐张照片枚举目录在大相册上是平方级开销。目录无法访问时视为没有同名视频。
    /// </remarks>
    private static Dictionary<string, string> FindCompanionVideos(IReadOnlyList<string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(file => Path.GetDirectoryName(Path.GetFullPath(file))!, StringComparer.Ordinal))
        {
            var videos = IndexVideosByStem(group.Key);
            foreach (var file in group)
            {
                if (videos.TryGetValue(Path.GetFileNameWithoutExtension(file), out var video))
                {
                    result[file] = video;
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> IndexVideosByStem(string directory)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (!CompanionExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(path);
                if (!index.TryGetValue(stem, out var existing) || MediaFileTypes.VideoRank(path) < MediaFileTypes.VideoRank(existing))
                {
                    index[stem] = path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            index.Clear();
        }

        return index;
    }

    private static long? TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureValidImage(string path)
    {
        Span<byte> header = stackalloc byte[32];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (MediaFileTypes.DetectPhotoExtension(header[..read], string.Empty).Length == 0)
        {
            throw new InvalidDataException("生成的图片格式无效，已放弃替换。");
        }
    }
}
