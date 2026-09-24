namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 图片格式转换。实现需保留 EXIF、GPS 与 ICC，并保证输出在各类查看器中方向正确。
/// </summary>
public interface IImageConverter
{
    Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default);
}

/// <summary>
/// 视频容器转换；能流复制时不重新编码。
/// </summary>
public interface IVideoConverter
{
    /// <param name="sourcePath">源视频</param>
    /// <param name="destinationPath">输出 MP4</param>
    /// <param name="forceTranscode">强制重新编码（例如需要把镜像矩阵烧录进像素）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default);

    Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
