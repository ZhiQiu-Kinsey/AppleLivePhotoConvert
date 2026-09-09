using System.Collections.Concurrent;
using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.E2E.Harness;

/// <summary>
/// 高保真外部工具行为测试驱动：模拟 ExifTool、FFmpeg 与 heif-enc 的契约执行与元数据读写，
/// 严格跟踪调用与参数，同时生成真实合法的二进制载荷以供断言。
/// </summary>
public sealed class TestExifTool : IExifTool
{
    public ConcurrentDictionary<(string Path, ContentIdentifierKind Kind), string> ContentIdentifiers { get; } = new();
    public ConcurrentDictionary<string, DateTime> CreateDates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, TimeSpan> Durations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, long> MicroVideoOffsets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, bool> MirroredVideos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentBag<(string Path, long Offset)> WriteMotionPhotoCalls { get; } = [];
    public ConcurrentBag<string> RemoveMotionPhotoCalls { get; } = [];
    public ConcurrentBag<(string Source, string Dest)> CopyAllTagsCalls { get; } = [];
    public ConcurrentBag<(string Source, string Dest)> CopyCoverTagsCalls { get; } = [];
    public ConcurrentBag<(string Photo, string Id)> WriteApplePhotoCalls { get; } = [];
    public ConcurrentBag<(string Video, string? Photo, string Id)> WriteAppleVideoCalls { get; } = [];

    public Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default)
    {
        WriteMotionPhotoCalls.Add((imagePath, videoOffset));
        MicroVideoOffsets[imagePath] = videoOffset;
        return Task.CompletedTask;
    }

    public Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        RemoveMotionPhotoCalls.Add(imagePath);
        MicroVideoOffsets.TryRemove(imagePath, out _);
        return Task.CompletedTask;
    }

    public Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        CopyAllTagsCalls.Add((sourcePath, destinationPath));
        return Task.CompletedTask;
    }

    public Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        CopyCoverTagsCalls.Add((sourcePath, destinationPath));
        return Task.CompletedTask;
    }

    public Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (MicroVideoOffsets.TryGetValue(imagePath, out var offset))
        {
            return Task.FromResult<long?>(offset);
        }

        return Task.FromResult<long?>(null);
    }

    public Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default)
    {
        if (ContentIdentifiers.TryGetValue((filePath, kind), out var id))
        {
            return Task.FromResult<string?>(id);
        }

        return Task.FromResult<string?>(null);
    }

    public Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        WriteApplePhotoCalls.Add((photoPath, contentIdentifier));
        ContentIdentifiers[(photoPath, ContentIdentifierKind.Photo)] = contentIdentifier;
        return Task.CompletedTask;
    }

    public Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        WriteAppleVideoCalls.Add((videoPath, photoPath, contentIdentifier));
        ContentIdentifiers[(videoPath, ContentIdentifierKind.Video)] = contentIdentifier;
        return Task.CompletedTask;
    }

    public Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (CreateDates.TryGetValue(filePath, out var dt))
        {
            return Task.FromResult<DateTime?>(dt);
        }

        if (File.Exists(filePath))
        {
            return Task.FromResult<DateTime?>(File.GetCreationTimeUtc(filePath));
        }

        return Task.FromResult<DateTime?>(null);
    }

    public Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (Durations.TryGetValue(filePath, out var ts))
        {
            return Task.FromResult<TimeSpan?>(ts);
        }

        return Task.FromResult<TimeSpan?>(null);
    }

    public Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MirroredVideos.TryGetValue(videoPath, out var mirrored) && mirrored);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 图像转换测试驱动
/// </summary>
public sealed class TestImageConverter : IImageConverter
{
    public ConcurrentBag<(string Source, string Dest, int Quality)> ConvertToHeicCalls { get; } = [];
    public ConcurrentBag<(string Source, string Dest)> ConvertToJpegCalls { get; } = [];

    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ConvertToJpegCalls.Add((sourcePath, destinationPath));
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var jpeg = SyntheticMediaFactory.CreateJpeg();
        File.WriteAllBytes(destinationPath, jpeg);
        return Task.CompletedTask;
    }

    public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
    {
        ConvertToHeicCalls.Add((sourcePath, destinationPath, quality));
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var heic = SyntheticMediaFactory.CreateHeic(512);
        File.WriteAllBytes(destinationPath, heic);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 视频转换测试驱动
/// </summary>
public sealed class TestVideoConverter : IVideoConverter
{
    public ConcurrentBag<(string Source, string Dest, bool ForceTranscode)> ConvertToMp4Calls { get; } = [];
    public ConcurrentBag<(string Source, string Dest)> RemuxToMovCalls { get; } = [];

    public Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default)
    {
        ConvertToMp4Calls.Add((sourcePath, destinationPath, forceTranscode));
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var mp4 = SyntheticMediaFactory.CreateMp4();
        File.WriteAllBytes(destinationPath, mp4);
        return Task.CompletedTask;
    }

    public Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        RemuxToMovCalls.Add((sourcePath, destinationPath));
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var mov = SyntheticMediaFactory.CreateMov();
        File.WriteAllBytes(destinationPath, mov);
        return Task.CompletedTask;
    }
}
