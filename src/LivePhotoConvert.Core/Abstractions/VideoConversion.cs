namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 视频转换选项。
/// </summary>
public sealed record VideoConversionOptions
{
    public static VideoConversionOptions Default { get; } = new();

    /// <summary>跳过流复制，直接重新编码。</summary>
    public bool ForceTranscode { get; init; }

    /// <summary>
    /// 把显示矩阵（旋转与镜像）烧录进像素并重新编码；安卓相册普遍忽略镜像矩阵。
    /// 为 false 时重新编码也把显示矩阵原样保留为元数据，与流复制一致。
    /// </summary>
    public bool BakeOrientation { get; init; }

    /// <summary>
    /// 缺少 10-bit HEVC 编码器时允许 HDR 源降级为 8-bit H.264（保留色彩标记，但会出现色带）。
    /// 为 false 时抛出 <see cref="VideoConversionException"/>（<see cref="VideoConversionError.HdrEncoderUnavailable"/>）。
    /// </summary>
    public bool AllowHdrDowngrade { get; init; }
}

/// <summary>视频转换失败的原因，供界面映射为本地化文案。</summary>
public enum VideoConversionError
{
    /// <summary>流复制与重新编码都失败。</summary>
    EncodeFailed,

    /// <summary>源是 HDR，但 FFmpeg 缺少保真所需的 10-bit HEVC 编码器（libx265）。</summary>
    HdrEncoderUnavailable
}

/// <summary>
/// 视频转换失败。<see cref="Exception.Message"/> 为中文诊断信息，界面应按 <see cref="Error"/> 显示本地化文案。
/// </summary>
public sealed class VideoConversionException(VideoConversionError error, string message) : InvalidOperationException(message)
{
    public VideoConversionError Error { get; } = error;
}
