using ImageMagick;
using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 进程内的图片转换（Magick.NET），在没有 heif-enc 时作为 HEIC 编码的后备。
/// </summary>
/// <remarks>
/// HEIF 查看器依据容器内的 irot/imir 而不是 EXIF 方向旋转图像，Magick 写 HEIC 时不会生成这些属性，
/// 因此两种输出都先把像素转正并把方向写为 1，保证在任何查看器中方向一致。
/// </remarks>
public sealed class MagickImageConverter : IImageConverter
{
    public static MagickImageConverter Instance { get; } = new();

    /// <summary>当前平台的 Magick.NET 原生库是否带有 HEIC 编码器。</summary>
    public static bool SupportsHeicEncoding { get; } =
        MagickNET.SupportedFormats.Any(format => format.Format == MagickFormat.Heic && format.SupportsWriting);

    public Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ConvertAsync(sourcePath, destinationPath, MagickFormat.Jpeg, 95, cancellationToken);

    public Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default) =>
        SupportsHeicEncoding
            ? ConvertAsync(sourcePath, destinationPath, MagickFormat.Heic, quality, cancellationToken)
            : throw new InvalidOperationException("当前环境没有可用的 HEIC 编码器，请在「依赖引擎」页面安装 heif-enc。");

    private static Task ConvertAsync(string sourcePath, string destinationPath, MagickFormat format, int quality, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = new MagickImage(sourcePath);
            image.AutoOrient();
            image.Quality = (uint)Math.Clamp(quality, 1, 100);
            image.Write(destinationPath, format);
        }, cancellationToken);
}
