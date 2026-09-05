using ImageMagick;
using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 基于 Magick.NET 的图像转换器
/// </summary>
public sealed class MagickImageConverter : IImageConverter
{
    /// <summary>
    /// 单例实例
    /// </summary>
    public static MagickImageConverter Instance { get; } = new();

    /// <inheritdoc />
    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var image = new MagickImage(sourcePath);
        // 自动根据图像内置 EXIF 朝向或 Transform 旋转物理像素，确保像素在内存中 100% 物理转正
        image.AutoOrient();
        image.Format = MagickFormat.Jpeg;
        image.Quality = 95;
        image.Write(destinationPath);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var image = new MagickImage(sourcePath);
        image.Format = MagickFormat.Heic;
        image.Quality = (uint)quality;
        image.Write(destinationPath);

        return Task.CompletedTask;
    }
}
