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
        image.Format = MagickFormat.Jpeg;
        image.Quality = 95;
        image.Write(destinationPath);

        return Task.CompletedTask;
    }
}
