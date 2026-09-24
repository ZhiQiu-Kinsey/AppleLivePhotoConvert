using System.Collections.Concurrent;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 元数据服务的内存实现：读取返回预设值；动态照片 XMP 的写入与清除真实改写 JPEG 的 XMP 段，
/// 以便被测流程能用 <see cref="MotionPhotoLayout"/> 校验输出。
/// </summary>
internal sealed class FakeMetadataService : IMetadataService
{
    public ConcurrentDictionary<string, MediaMetadata> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentBag<string> CoverCopies { get; } = [];

    public ConcurrentDictionary<string, string> ApplePhotoIdentifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, AppleVideoTags> AppleVideoTags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentBag<(string Path, long VideoLength, long TimestampUs)> MotionPhotoWrites { get; } = [];

    /// <summary>对匹配的文件写元数据时抛出异常，模拟 ExifTool 失败。</summary>
    public Func<string, bool> FailWrites { get; set; } = _ => false;

    /// <summary>对匹配的文件读取 XMP 时抛出异常，模拟 ExifTool 读取失败。</summary>
    public Func<string, bool> FailXmpReads { get; set; } = _ => false;

    /// <summary>批量读取前同步执行：可抛出异常模拟整批读取失败，或阻塞以模拟耗时的读取。</summary>
    public Action<IReadOnlyCollection<string>>? BeforeRead { get; set; }

    public MetadataScope? LastScope { get; private set; }

    public void Set(string path, MediaMetadata metadata) => Tags[Path.GetFullPath(path)] = metadata with { Path = path };

    public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadAsync(IReadOnlyCollection<string> paths, MetadataScope scope = MetadataScope.Standard, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeRead?.Invoke(paths);
        LastScope = scope;
        IReadOnlyDictionary<string, MediaMetadata> result = paths.Distinct().ToDictionary(
            path => path,
            path => Tags.TryGetValue(Path.GetFullPath(path), out var metadata) ? metadata with { Path = path } : MediaMetadata.Empty(path));
        return Task.FromResult(result);
    }

    public Task<string?> ReadXmpAsync(string path, CancellationToken cancellationToken = default)
    {
        if (FailXmpReads(path))
        {
            throw new InvalidOperationException($"模拟 ExifTool 读取失败：{Path.GetFileName(path)}");
        }

        using var stream = File.OpenRead(path);
        return Task.FromResult(MotionPhotoLayout.ReadJpegXmp(stream));
    }

    public async Task WriteMotionPhotoAsync(string coverPath, long videoLength, long presentationTimestampUs, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(coverPath);
        var xmp = MotionPhotoXmp.Apply(await ReadXmpAsync(coverPath, cancellationToken), videoLength, presentationTimestampUs);
        await File.WriteAllBytesAsync(coverPath, SyntheticMedia.InsertXmp(await File.ReadAllBytesAsync(coverPath, cancellationToken), xmp), cancellationToken);
        MotionPhotoWrites.Add((coverPath, videoLength, presentationTimestampUs));
    }

    public async Task RemoveMotionPhotoAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(imagePath);
        if (MotionPhotoXmp.Remove(await ReadXmpAsync(imagePath, cancellationToken)) is { } xmp)
        {
            await File.WriteAllBytesAsync(imagePath, SyntheticMedia.InsertXmp(await File.ReadAllBytesAsync(imagePath, cancellationToken), xmp), cancellationToken);
        }
    }

    public Task CopyCoverMetadataAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(destinationPath);
        CoverCopies.Add(sourcePath);
        return Task.CompletedTask;
    }

    public Task WriteApplePhotoIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(photoPath);
        ApplePhotoIdentifiers[photoPath] = contentIdentifier;
        return Task.CompletedTask;
    }

    public Task WriteAppleVideoTagsAsync(string videoPath, AppleVideoTags tags, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing(videoPath);
        AppleVideoTags[videoPath] = tags;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ThrowIfFailing(string path)
    {
        if (FailWrites(path))
        {
            throw new InvalidOperationException($"模拟 ExifTool 写入失败：{Path.GetFileName(path)}");
        }
    }
}

internal sealed class FakeImageConverter : IImageConverter
{
    public ConcurrentBag<string> JpegConversions { get; } = [];

    public ConcurrentBag<string> HeicConversions { get; } = [];

    public int HeicSize { get; init; } = 1024;

    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        JpegConversions.Add(sourcePath);
        return File.WriteAllBytesAsync(destinationPath, SyntheticMedia.Jpeg(), cancellationToken);
    }

    public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default)
    {
        HeicConversions.Add(sourcePath);
        return File.WriteAllBytesAsync(destinationPath, SyntheticMedia.Heic(HeicSize), cancellationToken);
    }
}

internal sealed class FakeVideoConverter : IVideoConverter
{
    public ConcurrentBag<(string Source, VideoConversionOptions Options)> Mp4Conversions { get; } = [];

    public ConcurrentBag<string> MovRemuxes { get; } = [];

    /// <summary>不为 <c>null</c> 时每次转换都抛出该异常。</summary>
    public Exception? Failure { get; set; }

    public Task ConvertToMp4Async(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Mp4Conversions.Add((sourcePath, options ?? VideoConversionOptions.Default));
        return File.WriteAllBytesAsync(destinationPath, SyntheticMedia.Mp4((int)new FileInfo(sourcePath).Length), cancellationToken);
    }

    public Task RemuxToMovAsync(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        MovRemuxes.Add(sourcePath);
        return File.WriteAllBytesAsync(destinationPath, SyntheticMedia.Mov((int)new FileInfo(sourcePath).Length), cancellationToken);
    }
}
