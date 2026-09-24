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
    /// <summary>HEIC 质量 90 时相对 JPEG 的典型体积比例，用于预估。</summary>
    private const double HeicSizeRatio = 0.45;

    public long OriginalBytes => ImageBytes + CompanionBytes;

    public long VideoBytes => (EmbeddedVideo?.Length ?? 0) + CompanionBytes;

    public bool HasVideo => EmbeddedVideo is not null || CompanionVideo is not null;

    public bool WillConvert(bool convertToHeic) => convertToHeic && !HasGainMap && !MediaFileTypes.IsHeic(ImagePath);

    public long EstimateFinalBytes(bool convertToHeic)
    {
        var photoBytes = ImageBytes - (EmbeddedVideo?.Length ?? 0);
        return WillConvert(convertToHeic) ? (long)(photoBytes * HeicSizeRatio) : photoBytes;
    }
}

/// <summary>
/// 空间瘦身：剥离动态照片的内嵌视频或 Apple 实况照片的配对视频，并可选转码为 HEIC。
/// </summary>
public sealed class MotionPhotoStripper(IMetadataService metadata, IImageConverter imageConverter)
{
    private static readonly string[] CompanionExtensions = [".mov", ".mp4"];

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
    /// </summary>
    public async Task<IReadOnlyList<StripCandidate>> AnalyzeAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var companions = files.Select(file => (Image: file, Video: FindCompanionVideo(file)))
                              .Where(x => x.Video is not null)
                              .ToDictionary(x => x.Image, x => x.Video!, StringComparer.Ordinal);
        var tags = companions.Count == 0
            ? new Dictionary<string, MediaMetadata>()
            : await metadata.ReadAsync([.. companions.Keys, .. companions.Values], MetadataScope.Standard, cancellationToken);

        var results = new StripCandidate[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ConversionDefaults.Parallelism, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var file = files[index];
                var layout = await ImageInspector.InspectAsync(file, metadata, token);
                var companion = layout.Video is null
                                && companions.TryGetValue(file, out var video)
                                && PairValidator.Validate(tags[file], tags[video]).IsAccepted
                    ? video
                    : null;
                results[index] = new StripCandidate(file, new FileInfo(file).Length, layout.Video, companion, companion is null ? 0 : new FileInfo(companion).Length, layout.HasGainMap);
            });

        return results;
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
                await BinaryFile.CopySegmentAsync(source, clean, 0, embedded.Offset, cancellationToken);
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
                timestamp.ApplyTo(staged);
                final = inPlace
                    ? committer.ReplaceSource(staged, source, extension)
                    : committer.Commit(staged, directory, Path.GetFileNameWithoutExtension(source) + extension);
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
    /// 同目录下的同名视频（IMG_0001.HEIC 对应 IMG_0001.MOV）；是否真的属于该照片需要再做配对校验。
    /// </summary>
    /// <remarks>枚举目录而不是逐个 File.Exists，返回磁盘上的真实文件名（Windows 上扩展名大小写可能与探测值不同）。</remarks>
    private static string? FindCompanionVideo(string imagePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        return Directory.EnumerateFiles(directory, stem + ".*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
                        .Where(path => CompanionExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                                       && Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(MediaFileTypes.VideoRank)
                        .FirstOrDefault();
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
