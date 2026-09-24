using ImageMagick;

namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>
/// 平台已有的缩略图（如 Windows Shell 缩略图缓存），命中时免去解码原图。
/// </summary>
public interface IThumbnailSource
{
    /// <summary>
    /// 取不超出也不远小于 <paramref name="width"/>×<paramref name="height"/> 框的已转正缩略图，可以比框大。
    /// 拿不到（无缓存、无解码扩展、只能给出文件图标）返回 <c>null</c>，除取消外不抛异常。调用方负责释放返回的图像。
    /// </summary>
    Task<IMagickImage<byte>?> TryGetAsync(string path, int width, int height, CancellationToken cancellationToken);
}
