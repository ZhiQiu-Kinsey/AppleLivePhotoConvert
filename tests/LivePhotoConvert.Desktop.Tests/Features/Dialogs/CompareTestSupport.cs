using ImageMagick;
using ImageMagick.Drawing;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Tests.Features.Dialogs;

/// <summary>
/// 用 Magick 以低质量 JPEG 代替 HEIC 编码：产物可解码、压缩痕迹肉眼可见，不依赖外部工具。
/// 可设闸门让编码停在中途，以验证取消与清理。
/// </summary>
internal sealed class LossyStandInEncoder : IImageConverter
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>在产物末尾追加的字节数，模拟 HEIC 比原图还大的编码结果。</summary>
    public int PadBytes { get; init; }

    /// <summary>编码开始时的输出路径（位于样张的临时目录内）。</summary>
    public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ConvertToHeicAsync(sourcePath, destinationPath, 95, cancellationToken);

    public async Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Started.TrySetResult(destinationPath);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        await Task.Run(() =>
        {
            using var image = new MagickImage(sourcePath);
            image.AutoOrient();
            image.Quality = 20;
            image.Write(destinationPath, MagickFormat.Jpeg);
            if (PadBytes > 0)
            {
                using var stream = new FileStream(destinationPath, FileMode.Append);
                stream.Write(new byte[PadBytes]);
            }
        }, cancellationToken);
    }
}

/// <summary>记录元数据服务的创建与释放次数的引擎。</summary>
internal sealed class CountingEngines(IImageConverter images) : IConversionEngines
{
    private int _created;
    private int _disposed;

    public FakeMetadataService Metadata { get; } = new();

    public int MetadataCreated => Volatile.Read(ref _created);

    public int MetadataDisposed => Volatile.Read(ref _disposed);

    public IMetadataService CreateMetadata(ToolPaths tools, int parallelism)
    {
        Interlocked.Increment(ref _created);
        return new Tracked(Metadata, () => Interlocked.Increment(ref _disposed));
    }

    public IImageConverter CreateImageConverter(ToolPaths tools) => images;

    public IVideoConverter CreateVideoConverter(ToolPaths tools) => throw new NotSupportedException();

    public IAppleGainMapDecoder? CreateGainMapDecoder(ToolPaths tools) => null;

    private sealed class Tracked(FakeMetadataService inner, Action onDispose) : IMetadataService
    {
        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadAsync(IReadOnlyCollection<string> paths, MetadataScope scope = MetadataScope.Standard, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(paths, scope, cancellationToken);

        public Task<string?> ReadXmpAsync(string path, CancellationToken cancellationToken = default) => inner.ReadXmpAsync(path, cancellationToken);

        public Task WriteMotionPhotoAsync(string coverPath, long videoLength, long presentationTimestampUs, CancellationToken cancellationToken = default) =>
            inner.WriteMotionPhotoAsync(coverPath, videoLength, presentationTimestampUs, cancellationToken);

        public Task RemoveMotionPhotoAsync(string imagePath, CancellationToken cancellationToken = default) => inner.RemoveMotionPhotoAsync(imagePath, cancellationToken);

        public Task CopyCoverMetadataAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            inner.CopyCoverMetadataAsync(sourcePath, destinationPath, cancellationToken);

        public Task WriteApplePhotoIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default) =>
            inner.WriteApplePhotoIdentifierAsync(photoPath, contentIdentifier, cancellationToken);

        public Task WriteAppleVideoTagsAsync(string videoPath, AppleVideoTags tags, CancellationToken cancellationToken = default) =>
            inner.WriteAppleVideoTagsAsync(videoPath, tags, cancellationToken);

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class CompareSamples
{
    /// <summary>写入一张有细节（渐变、图形）的真实 JPEG 动态照片：封面 + Google XMP + 尾部 MP4。</summary>
    public static string WriteMotionPhoto(string path, int width = 1200, int height = 900, int videoBytes = 60_000, int seed = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage($"gradient:#1E3A8A-#F59E0B", (uint)width, (uint)height);
        new Drawables()
            .FillColor(new MagickColor("#E4572E"))
            .Circle(width * (0.3 + seed * 0.05), height / 2.0, width * (0.3 + seed * 0.05) + height / 5.0, height / 2.0)
            .FillColor(MagickColors.White)
            .Rectangle(width * 0.6, height * 0.2, width * 0.8, height * 0.4)
            .Draw(image);
        image.Quality = 92;
        var cover = image.ToByteArray(MagickFormat.Jpeg);
        File.WriteAllBytes(path, SyntheticMedia.MotionPhoto(cover, SyntheticMedia.Mp4(videoBytes)));
        return path;
    }

    public static StripSampler Sampler(IConversionEngines engines) => new(engines, new MetadataSessionPool(engines, TimeProvider.System));

    /// <summary>机器上同时有 ExifTool 与 heif-enc 时返回真实引擎，否则跳过用例。</summary>
    public static IConversionEngines RequireRealTools()
    {
        if (ToolLocator.Find(ExifToolMetadataService.ExecutableName) is null || ToolLocator.Find(HeifEncImageConverter.ExecutableName) is null)
        {
            Assert.Skip("未安装 ExifTool 或 heif-enc，跳过真实编码用例。");
        }

        return ExternalToolEngines.Instance;
    }
}
