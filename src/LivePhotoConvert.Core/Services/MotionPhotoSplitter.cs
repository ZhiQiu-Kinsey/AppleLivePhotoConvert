using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Services;

public enum SplitTarget
{
    /// <summary>无损切出封面与视频（.jpg/.heic + .mp4）。</summary>
    Extract,

    /// <summary>还原为 Apple 实况照片（.HEIC + .MOV），写入配对 ContentIdentifier。</summary>
    Apple
}

public sealed record SplitRequest
{
    public required IReadOnlyList<string> Files { get; init; }

    public required OutputOptions Output { get; init; }

    public SplitTarget Target { get; init; }

    public SourceFileAction SourceAction { get; init; }

    public int HeicQuality { get; init; } = ConversionDefaults.HeicQuality;

    public int Parallelism { get; init; } = ConversionDefaults.Parallelism;
}

/// <summary>
/// 拆分单文件动态照片：切出封面与视频，或进一步还原为 Apple 实况照片。
/// </summary>
public sealed class MotionPhotoSplitter(IMetadataService metadata, IImageConverter imageConverter, IVideoConverter? videoConverter)
{
    public async Task<BatchReport> SplitAsync(SplitRequest request, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target == SplitTarget.Apple && videoConverter is null)
        {
            throw new InvalidOperationException("还原为 Apple 实况照片需要 FFmpeg。");
        }

        if (request.Files.Count == 0)
        {
            return BatchReport.Empty;
        }

        var tags = request.Target == SplitTarget.Apple
            ? await metadata.ReadAsync(request.Files, MetadataScope.Standard, cancellationToken)
            : null;

        OutputCommitter.DeleteStaleStagingFiles(request.Output.Directory, ConversionDefaults.StaleStagingAge);
        var committer = new OutputCommitter(request.Output.Conflict, request.Files);
        var disposition = new SourceDisposition(request.SourceAction, SourceDisposition.SplitFolderName);
        using var workspace = new TempWorkspace();

        return await BatchRunner.RunAsync(
            request.Files,
            path => path,
            async (path, token) =>
            {
                var layout = await ImageInspector.InspectAsync(path, metadata, token);
                if (layout.Video is not { } video)
                {
                    return ItemOutcome.Skipped(path, "不是动态照片（未找到内嵌视频）");
                }

                var directory = request.Output.DirectoryFor(path);
                var outputs = request.Target == SplitTarget.Apple
                    ? await RestoreAppleAsync(path, video, directory, request.HeicQuality, tags![path], committer, workspace, token)
                    : await ExtractAsync(path, video, directory, committer, token);
                return ItemOutcome.Succeeded(path, outputs) with { CleanupError = disposition.Apply(path) };
            },
            request.Parallelism,
            progress,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ExtractAsync(string path, EmbeddedVideo video, string directory, OutputCommitter committer, CancellationToken cancellationToken)
    {
        var photoExtension = ImageInspector.SniffExtension(path, 0, header => MediaFileTypes.DetectPhotoExtension(header, Path.GetExtension(path)));
        var videoExtension = ImageInspector.SniffExtension(path, video.Offset, header => MediaFileTypes.DetectVideoExtension(header));
        var stagedPhoto = OutputCommitter.CreateStagingPath(directory, photoExtension);
        var stagedVideo = OutputCommitter.CreateStagingPath(directory, videoExtension);
        try
        {
            await BinaryFile.CopySegmentAsync(path, stagedPhoto, 0, video.ImageEnd, cancellationToken);
            await metadata.RemoveMotionPhotoAsync(stagedPhoto, cancellationToken);
            await BinaryFile.CopySegmentAsync(path, stagedVideo, video.Offset, video.Length, cancellationToken);

            var stem = Path.GetFileNameWithoutExtension(path);
            return committer.CommitGroup(
                [new StagedFile(stagedPhoto, stem + photoExtension), new StagedFile(stagedVideo, stem + videoExtension)],
                directory,
                FileTimestamp.Read(path));
        }
        catch
        {
            FileHelper.TryDeleteFile(stagedPhoto);
            FileHelper.TryDeleteFile(stagedVideo);
            throw;
        }
    }

    private async Task<IReadOnlyList<string>> RestoreAppleAsync(
        string path,
        EmbeddedVideo video,
        string directory,
        int heicQuality,
        MediaMetadata sourceTags,
        OutputCommitter committer,
        TempWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var photoExtension = ImageInspector.SniffExtension(path, 0, header => MediaFileTypes.DetectPhotoExtension(header, Path.GetExtension(path)));
        var videoExtension = ImageInspector.SniffExtension(path, video.Offset, header => MediaFileTypes.DetectVideoExtension(header));
        var cover = workspace.NewFile(photoExtension);
        var embeddedVideo = workspace.NewFile(videoExtension);
        var stagedPhoto = OutputCommitter.CreateStagingPath(directory, ".HEIC");
        var stagedVideo = OutputCommitter.CreateStagingPath(directory, ".MOV");
        try
        {
            await BinaryFile.CopySegmentAsync(path, cover, 0, video.ImageEnd, cancellationToken);
            await metadata.RemoveMotionPhotoAsync(cover, cancellationToken);
            if (photoExtension.Equals(".heic", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(cover, stagedPhoto);
            }
            else
            {
                await imageConverter.ConvertToHeicAsync(cover, stagedPhoto, heicQuality, cancellationToken);
            }

            await BinaryFile.CopySegmentAsync(path, embeddedVideo, video.Offset, video.Length, cancellationToken);
            await videoConverter!.RemuxToMovAsync(embeddedVideo, stagedVideo, cancellationToken);

            var contentIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
            await metadata.WriteApplePhotoIdentifierAsync(stagedPhoto, contentIdentifier, cancellationToken);
            await metadata.WriteAppleVideoTagsAsync(stagedVideo, AppleVideoTags.From(contentIdentifier, sourceTags), cancellationToken);

            var stem = Path.GetFileNameWithoutExtension(path);
            return committer.CommitGroup(
                [new StagedFile(stagedPhoto, stem + ".HEIC"), new StagedFile(stagedVideo, stem + ".MOV")],
                directory,
                FileTimestamp.Read(path));
        }
        catch
        {
            FileHelper.TryDeleteFile(stagedPhoto);
            FileHelper.TryDeleteFile(stagedVideo);
            throw;
        }
        finally
        {
            FileHelper.TryDeleteFile(cover);
            FileHelper.TryDeleteFile(embeddedVideo);
        }
    }
}
