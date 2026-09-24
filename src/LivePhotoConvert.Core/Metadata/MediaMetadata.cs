using System.Globalization;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 一个媒体文件中转换流程需要的元数据。
/// </summary>
public sealed record MediaMetadata
{
    public required string Path { get; init; }

    public CaptureTime? CaptureTime { get; init; }

    /// <summary>Apple 实况照片的配对标识（照片 MakerNotes / 视频 QuickTime Keys）。</summary>
    public string? ContentIdentifier { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>视频任一轨道的变换矩阵带镜像（前置摄像头），需要重新编码才能在安卓相册正确显示。</summary>
    public bool IsMirrored { get; init; }

    /// <summary>Apple 实况视频中封面帧的时间点（微秒），来自 StillImageTime 所在的 timed-metadata 轨道。</summary>
    public long? StillImageTimeUs { get; init; }

    public GeoLocation? Location { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Software { get; init; }

    /// <summary>Apple MakerNote 0x21（HDRHeadroom，Apple 文档中的 maker33），与 <see cref="AppleHdrGain"/> 一起决定增益图的 HDR 余量。</summary>
    public double? AppleHdrHeadroom { get; init; }

    /// <summary>Apple MakerNote 0x30（HDRGain，Apple 文档中的 maker48）。</summary>
    public double? AppleHdrGain { get; init; }

    /// <summary>HEIC 带 Apple HDR 增益图辅助图像（urn:com:apple:photo:2020:aux:hdrgainmap）。</summary>
    public bool HasAppleGainMap { get; init; }

    /// <summary>XMP-HDRGainMap:HDRGainMapVersion；Apple 用它声明文件带增益图。</summary>
    public long? HdrGainMapVersion { get; init; }

    public static MediaMetadata Empty(string path) => new() { Path = path };
}

/// <summary>
/// 带符号的十进制经纬度与可选海拔。
/// </summary>
public readonly record struct GeoLocation(double Latitude, double Longitude, double? Altitude)
{
    /// <summary>
    /// QuickTime Keys:GPSCoordinates 的写入格式：<c>纬度, 经度[, 海拔]</c>。
    /// </summary>
    public string ToQuickTimeString() => Altitude is { } altitude
        ? string.Create(CultureInfo.InvariantCulture, $"{Latitude}, {Longitude}, {altitude}")
        : string.Create(CultureInfo.InvariantCulture, $"{Latitude}, {Longitude}");
}
