using System.Globalization;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.UltraHdr;
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

    /// <summary><see cref="SourceFileAction.Move"/> 时源文件移入的子文件夹名，由调用方按界面语言提供。</summary>
    public string? ArchiveFolderName { get; init; }

    public bool SkipValidation { get; init; }

    public int Parallelism { get; init; } = ConversionDefaults.Parallelism;

    /// <summary>
    /// 源 HEIC 带 Apple HDR 增益图时把封面组装为 Ultra HDR（需要 heif-dec）；无法保留时输出 SDR 封面，
    /// 原因记录在 <see cref="ItemOutcome.Notes"/> 中。
    /// </summary>
    public bool PreserveHdr { get; init; } = true;
}

/// <summary>
/// 把 iPhone 实况照片（照片 + 视频）合成为 Google 规范的单文件动态照片（JPEG + 追加的 MP4），
/// 并写入小米相册需要的 EXIF 0x8897 标记。源 HEIC 带 Apple HDR 增益图时，封面组装为 Ultra HDR（主图 + 增益图 + 视频）。
/// </summary>
/// <param name="metadata">元数据读写</param>
/// <param name="imageConverter">SDR 封面转码</param>
/// <param name="videoConverter">视频转码</param>
/// <param name="gainMapDecoder">HEIC 主图与增益图解码；为 <c>null</c> 时带增益图的照片也输出 SDR 封面</param>
public sealed class MotionPhotoMerger(
    IMetadataService metadata,
    IImageConverter imageConverter,
    IVideoConverter videoConverter,
    IAppleGainMapDecoder? gainMapDecoder = null)
{
    /// <summary>余量低于此值时增益图几乎不产生 HDR 效果，不值得增加体积与兼容性风险。</summary>
    private const double MinimumHeadroom = 1.01;

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
        var disposition = new SourceDisposition(request.SourceAction, request.ArchiveFolderName);
        using var workspace = new TempWorkspace();

        return await BatchRunner.RunAsync(
            chosen,
            pair => pair.PhotoPath,
            (pair, token) => MergeOneAsync(pair, names[pair], tags, request, committer, disposition, workspace, token),
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
            forced.Contains(pair) ? PairValidationResult.Accept(OutcomeReason.PairManuallyConfirmed)
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
                skipped.Add(ItemOutcome.Skipped(pair.PhotoPath, result.Causes));
            }
        }

        foreach (var group in candidates.Where(pair => !pair.IsContentIdentifierMatched).GroupBy(pair => pair.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var results = group.Select(pair => (Pair: pair, Result: Validate(pair))).ToList();
            var selected = results.FirstOrDefault(x => forced.Contains(x.Pair)).Pair ?? results.FirstOrDefault(x => x.Result.IsAccepted).Pair;
            if (selected is null)
            {
                skipped.Add(ItemOutcome.Skipped(group.First().PhotoPath, [.. results.SelectMany(x => x.Result.Causes).Distinct()]));
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
        MergeRequest request,
        OutputCommitter committer,
        SourceDisposition disposition,
        TempWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if ((MissingOrEmpty(pair.PhotoPath) ?? MissingOrEmpty(pair.VideoPath)) is { } missing)
        {
            return ItemOutcome.Failed(pair.PhotoPath, new OutcomeCause(OutcomeReason.SourceMissingOrEmpty, Path.GetFileName(missing)));
        }

        var videoTags = tags[pair.VideoPath];
        var notes = new List<OutcomeNote>();

        // 安卓动态照片规范要求封面为 JPEG；复制一份是因为接下来要改写封面的 XMP
        var cover = workspace.NewFile(".jpg");
        string? decodeDirectory = null;
        string? ultraHdrCover = null;
        string? convertedVideo = null;
        string? staging = null;
        try
        {
            if (MediaFileTypes.IsJpeg(pair.PhotoPath))
            {
                File.Copy(pair.PhotoPath, cover);
            }
            else
            {
                (string GainMap, double Headroom)? hdr = null;
                if (request.PreserveHdr && MediaFileTypes.IsHeic(pair.PhotoPath))
                {
                    decodeDirectory = workspace.NewFile(string.Empty);
                    if (await TryDecodeGainMapAsync(pair.PhotoPath, tags[pair.PhotoPath], decodeDirectory, notes, cancellationToken) is { } decoded)
                    {
                        // 主图与增益图出自同一次解码，方向一致；主图直接作为封面，不再另行转码
                        File.Move(decoded.Images.PrimaryPath, cover);
                        hdr = (decoded.Images.GainMapPath, decoded.Headroom);
                    }
                }

                if (hdr is null)
                {
                    await imageConverter.ConvertToJpegAsync(pair.PhotoPath, cover, cancellationToken);
                }

                await metadata.CopyCoverMetadataAsync(pair.PhotoPath, cover, cancellationToken);
                if (hdr is { } gainMap)
                {
                    ultraHdrCover = await TryAssembleUltraHdrAsync(cover, gainMap.GainMap, gainMap.Headroom, workspace, notes, cancellationToken);
                }
            }

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
            var timestampUs = videoTags.StillImageTimeUs ?? 0;
            var finalCover = ultraHdrCover is not null && await TryWriteUltraHdrMotionPhotoAsync(ultraHdrCover, videoLength, timestampUs, notes, cancellationToken)
                ? ultraHdrCover
                : cover;
            if (finalCover == cover)
            {
                await metadata.WriteMotionPhotoAsync(cover, videoLength, timestampUs, cancellationToken);
            }

            var directory = request.Output.DirectoryFor(pair.PhotoPath);
            staging = OutputCommitter.CreateStagingPath(directory, ".jpg");
            var (photoLength, _) = await BinaryFile.ConcatAsync(finalCover, video, staging, cancellationToken);
            if (MotionPhotoLayout.Locate(staging) is not { } located || located.ImageEnd != photoLength || located.Offset != photoLength || located.Length != videoLength)
            {
                throw new OutcomeException(OutcomeReason.VerificationFailed, "合成结果校验失败：无法在输出文件中按声明的位置定位到内嵌视频。");
            }

            if (finalCover == ultraHdrCover)
            {
                if (UltraHdrJpegWriter.Inspect(staging) is not { IsPrimaryLengthConsistent: true } layout || layout.ImageEnd != photoLength || !MotionPhotoLayout.Inspect(staging).HasGainMap)
                {
                    throw new OutcomeException(OutcomeReason.VerificationFailed, "合成结果校验失败：增益图位置与声明不一致。");
                }

                notes.Add(new OutcomeNote(OutcomeNoteKind.UltraHdrWritten));
            }

            var final = committer.Commit(staging, directory, $"MVIMG_{baseName}.jpg", FileTimestamp.Earliest(pair.PhotoPath, pair.VideoPath));
            staging = null;
            return ItemOutcome.Succeeded(pair.PhotoPath, final) with { CleanupError = disposition.Apply(pair.PhotoPath, pair.VideoPath), Notes = notes };
        }
        finally
        {
            FileHelper.TryDeleteFile(staging);
            FileHelper.TryDeleteFile(cover);
            FileHelper.TryDeleteFile(ultraHdrCover);
            FileHelper.TryDeleteFile(convertedVideo);
            FileHelper.TryDeleteDirectory(decodeDirectory);
        }
    }

    /// <summary>
    /// 判断能否保留 HDR 并解码主图与增益图；不能时记录原因并返回 <c>null</c>，调用方改走 SDR 流程。
    /// </summary>
    private async Task<(AppleGainMapImages Images, double Headroom)?> TryDecodeGainMapAsync(
        string photoPath,
        MediaMetadata photoTags,
        string outputDirectory,
        List<OutcomeNote> notes,
        CancellationToken cancellationToken)
    {
        if (!photoTags.HasAppleGainMap && photoTags.HdrGainMapVersion is null)
        {
            notes.Add(MissingGainMapNote(photoPath));
            return null;
        }

        if (photoTags.AppleHdrHeadroom is not { } maker33 || photoTags.AppleHdrGain is not { } maker48)
        {
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrMetadataMissing, "HDRHeadroom / HDRGain"));
            return null;
        }

        var headroom = AppleHdrHeadroom.Compute(maker33, maker48);
        if (headroom < MinimumHeadroom)
        {
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrMetadataMissing, string.Create(CultureInfo.InvariantCulture, $"headroom={headroom:0.####}")));
            return null;
        }

        if (gainMapDecoder is null)
        {
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrDecoderUnavailable));
            return null;
        }

        try
        {
            if (await gainMapDecoder.DecodeAsync(photoPath, outputDirectory, cancellationToken) is { } images)
            {
                return (images, headroom);
            }

            notes.Add(MissingGainMapNote(photoPath));
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrConversionFailed, ex.Message));
        }

        FileHelper.TryDeleteDirectory(outputDirectory);
        return null;
    }

    /// <summary>
    /// 换算增益图并组装 Ultra HDR 封面；失败时记录原因并返回 <c>null</c>，SDR 封面保持不变。
    /// </summary>
    private static async Task<string?> TryAssembleUltraHdrAsync(
        string cover,
        string appleGainMap,
        double headroom,
        TempWorkspace workspace,
        List<OutcomeNote> notes,
        CancellationToken cancellationToken)
    {
        var gainMap = workspace.NewFile(".jpg");
        var output = workspace.NewFile(".jpg");
        try
        {
            await AppleGainMapConverter.ConvertAsync(appleGainMap, gainMap, headroom, cancellationToken);
            await UltraHdrJpegWriter.WriteAsync(cover, gainMap, GainMapMetadata.FromAppleHeadroom(headroom), output, cancellationToken);
            return output;
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            FileHelper.TryDeleteFile(output);
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrConversionFailed, ex.Message));
            return null;
        }
        finally
        {
            FileHelper.TryDeleteFile(gainMap);
        }
    }

    /// <summary>
    /// 在 Ultra HDR 封面上写入动态照片声明（目录依次为 Primary、GainMap、MotionPhoto），修正 ExifTool 改写后过时的
    /// MPF 主图长度并回读校验；失败时记录原因，调用方改用 SDR 封面。
    /// </summary>
    private async Task<bool> TryWriteUltraHdrMotionPhotoAsync(string cover, long videoLength, long timestampUs, List<OutcomeNote> notes, CancellationToken cancellationToken)
    {
        try
        {
            await metadata.WriteMotionPhotoAsync(cover, videoLength, timestampUs, cancellationToken);
            UltraHdrJpegWriter.RefreshPrimaryLength(cover);
            UltraHdrJpegWriter.Verify(cover);
            return true;
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            notes.Add(new OutcomeNote(OutcomeNoteKind.HdrConversionFailed, ex.Message));
            return false;
        }
    }

    private static OutcomeNote MissingGainMapNote(string photoPath) =>
        new(HeifItems.HasToneMapItem(photoPath) ? OutcomeNoteKind.HdrToneMapNotSupported : OutcomeNoteKind.HdrGainMapMissing);

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

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

    private static string? MissingOrEmpty(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 ? null : path;
    }
}
