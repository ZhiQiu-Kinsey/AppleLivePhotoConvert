namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 图片格式转换
/// </summary>
public interface IImageConverter
{
    /// <summary>
    /// 将图片转换为 JPEG
    /// </summary>
    /// <param name="sourcePath">源图片路径</param>
    /// <param name="destinationPath">目标路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将图片转换为 HEIC 格式（高效率图像编码，体积约为 JPEG 的 40%–60%，画质几乎无损）
    /// </summary>
    /// <param name="sourcePath">源图片路径</param>
    /// <param name="destinationPath">目标路径</param>
    /// <param name="quality">压缩质量 (1–100)，默认 90</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality = 90, CancellationToken cancellationToken = default);
}

/// <summary>
/// 视频格式转换
/// </summary>
public interface IVideoConverter
{
    /// <summary>
    /// 将视频转换为 MP4
    /// </summary>
    /// <remarks>优先无损换容器，失败时回退到重新编码。</remarks>
    /// <param name="sourcePath">源视频路径</param>
    /// <param name="destinationPath">目标路径</param>
    /// <param name="forceTranscode">强制重新编码；前置摄像头视频带镜像矩阵时需转码烧入方向</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ConvertToMp4Async(string sourcePath, string destinationPath, bool forceTranscode = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将视频转换为 Apple QuickTime MOV 格式
    /// </summary>
    /// <remarks>优先无损换容器，失败时回退到重新编码。</remarks>
    /// <param name="sourcePath">源视频路径</param>
    /// <param name="destinationPath">目标路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RemuxToMovAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
