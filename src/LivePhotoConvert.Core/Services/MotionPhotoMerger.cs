using System.Globalization;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 合成动态照片时的输出文件名规则。
/// </summary>
public enum MergeNamingFormat
{
    /// <summary>MVIMG_{原文件名}.jpg</summary>
    Original = 0,

    /// <summary>MVIMG_{yyyyMMdd_HHmmss}_{原文件名}.jpg</summary>
    XiaomiWithOriginal = 1,

    /// <summary>MVIMG_{yyyyMMdd_HHmmss}.jpg，同一秒的多张依次追加序号</summary>
    XiaomiClean = 2
}

public sealed record MergeRequest
{
    public required IReadOnlyList<MediaPair> Candidates { get; init; }

    /// <summary>用户人工确认的配对，跳过校验并在同名候选中优先。</summary>
    public IReadOnlyCollection<MediaPair> ForceAccepted { get; init; } = [];

    public required OutputOptions Output { get; init; }

    public MergeNamingFormat Naming { get; init; }

    public SourceFileAction SourceAction { get; init; }

    public bool SkipValidation { get; init; }

    public int Parallelism { get; init; } = ConversionDefaults.Parallelism;
}

/// <summary>
/// 把 iPhone 实况照片（照片 + 视频）合成为 Google 规范的单文件动态照片（JPEG + 追加的 MP4），
/// 并写入小米相册需要的 EXIF 0x8897 标记。
/// </summary>
public sealed class MotionPhotoMerger(IMetadataService metadata, IImageConverter imageConverter, IVideoConverter videoConverter)
{
    public async Task<BatchReport> MergeAsync(MergeRequest request, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var forced = new HashSet<MediaPair>(request.ForceAccepted, MediaPairPathEqualityComparer.Instance);
        var candidates = request.Candidates.Concat(forced).Distinct(MediaPairPathEqualityComparer.Instance).ToList();
        if (candidates.Count == 0)
        {
            return BatchReport.Empty;
        }

        var files = candidates.SelectMany(pair => (string[])[pair.PhotoPath, pair.VideoPath]).Distinct(StringComparer.Ordinal).ToList();
        var tags = await metadata.ReadAsync(files, MetadataScope.StillImageTime, cancellationToken);
        var (chosen, skipped) = SelectPairs(candidates, forced, tags, request.SkipValidation);
        var names = chosen.ToDictionary(pair => pair, pair => ResolveBaseName(pair, tags, request.Naming));

        OutputCommitter.DeleteStaleStagingFiles(request.Output.Directory, ConversionDefaults.StaleStagingAge);
        var committer = new OutputCommitter(request.Output.Conflict, files);
        var disposition = new SourceDisposition(request.SourceAction, SourceDisposition.MergedFolderName);
        using var workspace = new TempWorkspace();

        return await BatchRunner.RunAsync(
            chosen,
            pair => pair.PhotoPath,
            (pair, token) => MergeOneAsync(pair, names[pair], tags, request.Output, committer, disposition, workspace, token),
            request.Parallelism,
            progress,
            skipped,
            cancellationToken);
    }

    /// <summary>
    /// ContentIdentifier 精确配对各自独立校验；同名候选按优先级排列，每组取人工确认的或第一个通过校验的。
    /// </summary>
    private static (List<MediaPair> Chosen, List<ItemOutcome> Skipped) SelectPairs(
        List<MediaPair> candidates,
        HashSet<MediaPair> forced,
        IReadOnlyDictionary<string, MediaMetadata> tags,
        bool skipValidation)
    {
        var chosen = new List<MediaPair>();
        var skipped = new List<ItemOutcome>();

        PairValidationResult Validate(MediaPair pair) =>
            forced.Contains(pair) ? PairValidationResult.Accept(["人工确认配对"])
            : skipValidation ? PairValidationResult.Accept()
            : PairValidator.Validate(tags[pair.PhotoPath], tags[pair.VideoPath]);

        foreach (var pair in candidates.Where(pair => pair.IsContentIdentifierMatched))
        {
            var result = Validate(pair);
            if (result.IsAccepted)
            {
                chosen.Add(pair);
            }
            else
            {
                skipped.Add(ItemOutcome.Skipped(pair.PhotoPath, result.Summary));
            }
        }

        foreach (var group in candidates.Where(pair => !pair.IsContentIdentifierMatched).GroupBy(pair => pair.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var results = group.Select(pair => (Pair: pair, Result: Validate(pair))).ToList();
            var selected = results.FirstOrDefault(x => forced.Contains(x.Pair)).Pair ?? results.FirstOrDefault(x => x.Result.IsAccepted).Pair;
            if (selected is null)
            {
                skipped.Add(ItemOutcome.Skipped(group.First().PhotoPath, string.Join("；", results.SelectMany(x => x.Result.Reasons).Distinct())));
            }
            else
            {
                chosen.Add(selected);
            }
        }

        return (chosen, skipped);
    }

    private async Task<ItemOutcome> MergeOneAsync(
        MediaPair pair,
        string baseName,
        IReadOnlyDictionary<string, MediaMetadata> tags,
        OutputOptions output,
        OutputCommitter committer,
        SourceDisposition disposition,
        TempWorkspace workspace,
        CancellationToken cancellationToken)
    {
        EnsureNotEmpty(pair.PhotoPath, "照片");
        EnsureNotEmpty(pair.VideoPath, "视频");
        var videoTags = tags[pair.VideoPath];

        // 安卓动态照片规范要求封面为 JPEG；复制一份是因为接下来要改写封面的 XMP
        var cover = workspace.NewFile(".jpg");
        if (MediaFileTypes.IsJpeg(pair.PhotoPath))
        {
            File.Copy(pair.PhotoPath, cover);
        }
        else
        {
            await imageConverter.ConvertToJpegAsync(pair.PhotoPath, cover, cancellationToken);
            await metadata.CopyCoverMetadataAsync(pair.PhotoPath, cover, cancellationToken);
        }

        string? convertedVideo = null;
        if (!MediaFileTypes.IsMp4(pair.VideoPath) || videoTags.IsMirrored)
        {
            convertedVideo = workspace.NewFile(".mp4");
            // 安卓相册普遍忽略镜像矩阵，前置镜像视频必须把方向烧录进像素
            var options = videoTags.IsMirrored ? new VideoConversionOptions { BakeOrientation = true } : null;
            await videoConverter.ConvertToMp4Async(pair.VideoPath, convertedVideo, options, cancellationToken);
        }

        var video = convertedVideo ?? pair.VideoPath;

        var videoLength = new FileInfo(video).Length;
        // Apple 的封面帧不一定在 1.5 秒处，必须用视频中记录的真实时间；没有记录时按小米约定写 0
        await metadata.WriteMotionPhotoAsync(cover, videoLength, videoTags.StillImageTimeUs ?? 0, cancellationToken);

        var directory = output.DirectoryFor(pair.PhotoPath);
        var staging = OutputCommitter.CreateStagingPath(directory, ".jpg");
        try
        {
            var (photoLength, _) = await BinaryFile.ConcatAsync(cover, video, staging, cancellationToken);
            if (MotionPhotoLayout.Locate(staging) is not { } located || located.ImageEnd != photoLength || located.Offset != photoLength || located.Length != videoLength)
            {
                throw new InvalidDataException("合成结果校验失败：无法在输出文件中按声明的位置定位到内嵌视频。");
            }

            var final = committer.Commit(staging, directory, $"MVIMG_{baseName}.jpg", FileTimestamp.Earliest(pair.PhotoPath, pair.VideoPath));
            return ItemOutcome.Succeeded(pair.PhotoPath, final) with { CleanupError = disposition.Apply(pair.PhotoPath, pair.VideoPath) };
        }
        catch
        {
            FileHelper.TryDeleteFile(staging);
            throw;
        }
        finally
        {
            FileHelper.TryDeleteFile(cover);
            FileHelper.TryDeleteFile(convertedVideo);
        }
    }

    private static string ResolveBaseName(MediaPair pair, IReadOnlyDictionary<string, MediaMetadata> tags, MergeNamingFormat naming)
    {
        if (naming == MergeNamingFormat.Original)
        {
            return pair.Name;
        }

        var time = ResolveCaptureTime(pair, tags).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return naming == MergeNamingFormat.XiaomiClean ? time : $"{time}_{pair.Name}";
    }

    /// <summary>
    /// 依次取照片拍摄时间、视频拍摄时间、文件名中的时间、文件系统最早时间（均为当地时间）。
    /// </summary>
    private static DateTime ResolveCaptureTime(MediaPair pair, IReadOnlyDictionary<string, MediaMetadata> tags)
    {
        if ((tags[pair.PhotoPath].CaptureTime ?? tags[pair.VideoPath].CaptureTime) is { } captureTime)
        {
            return captureTime.LocalTime;
        }

        if (FileNameDateTimeParser.TryParse(pair.PhotoPath, out var fromPhotoName) || FileNameDateTimeParser.TryParse(pair.VideoPath, out fromPhotoName))
        {
            return fromPhotoName;
        }

        var timestamp = FileTimestamp.Read(pair.PhotoPath);
        return (timestamp.CreationTimeUtc < timestamp.LastWriteTimeUtc ? timestamp.CreationTimeUtc : timestamp.LastWriteTimeUtc).ToLocalTime();
    }

    private static void EnsureNotEmpty(string path, string kind)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidDataException($"{kind}文件不存在或为空：{Path.GetFileName(path)}");
        }
    }
}
