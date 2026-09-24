namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 图片格式转换。实现需保留 EXIF、GPS 与 ICC，并保证输出在各类查看器中方向正确。
/// </summary>
public interface IImageConverter
{
    /// <exception cref="ImageConversionException">转换失败</exception>
    Task ConvertToJpegAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <exception cref="ImageConversionException">转换失败</exception>
    /// <exception cref="External.ToolNotFoundException">当前环境没有可用的 HEIC 编码器</exception>
    Task ConvertToHeicAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken = default);
}

/// <summary>
/// 图片转换失败（源图损坏、格式不受支持或编码器出错）。<see cref="Exception.Message"/> 只用于日志与详情，界面按原因码显示。
/// </summary>
public sealed class ImageConversionException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

/// <summary>
/// 视频容器转换；能流复制时不重新编码。HEVC 输出统一标记为 hvc1（iOS 不识别 hev1），HDR 源重新编码时保持 10-bit 与色彩元数据。
/// </summary>
public interface IVideoConverter
{
    /// <param name="sourcePath">源视频</param>
    /// <param name="destinationPath">输出 MP4</param>
    /// <param name="options">转换选项；null 表示 <see cref="VideoConversionOptions.Default"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VideoConversionException">转换失败</exception>
    Task ConvertToMp4Async(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default);

    /// <param name="sourcePath">源视频</param>
    /// <param name="destinationPath">输出给 iOS 用的 MOV</param>
    /// <param name="options">转换选项；null 表示 <see cref="VideoConversionOptions.Default"/></param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VideoConversionException">转换失败</exception>
    Task RemuxToMovAsync(string sourcePath, string destinationPath, VideoConversionOptions? options = null, CancellationToken cancellationToken = default);
}
