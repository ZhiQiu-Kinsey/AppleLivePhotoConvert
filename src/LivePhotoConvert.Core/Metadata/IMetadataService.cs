namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 读取范围：<see cref="Standard"/> 足以完成配对校验与命名；
/// <see cref="StillImageTime"/> 额外解析视频的 timed-metadata，只在合成时需要。
/// </summary>
public enum MetadataScope
{
    Standard,
    StillImageTime
}

/// <summary>
/// Apple 实况视频需要写入的元数据。
/// </summary>
public sealed record AppleVideoTags(string ContentIdentifier)
{
    public CaptureTime? CaptureTime { get; init; }

    public GeoLocation? Location { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Software { get; init; }

    public static AppleVideoTags From(string contentIdentifier, MediaMetadata? source) => new(contentIdentifier)
    {
        CaptureTime = source?.CaptureTime,
        Location = source?.Location,
        Make = source?.Make,
        Model = source?.Model,
        Software = source?.Software
    };
}

/// <summary>
/// 媒体元数据的读写。写操作只作用于流程自己生成的中间文件，从不直接修改用户的原文件。
/// </summary>
public interface IMetadataService : IAsyncDisposable
{
    /// <summary>
    /// 批量读取元数据；读取失败的文件返回空元数据而不是抛出异常。
    /// </summary>
    Task<IReadOnlyDictionary<string, MediaMetadata>> ReadAsync(IReadOnlyCollection<string> paths, MetadataScope scope = MetadataScope.Standard, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取文件的 XMP 包；没有 XMP 时返回 <c>null</c>。
    /// </summary>
    Task<string?> ReadXmpAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为即将拼接视频的 JPEG 封面写入 Google 动态照片 XMP 与小米 0x8897 标记，保留封面原有的其它 XMP。
    /// </summary>
    Task WriteMotionPhotoAsync(string coverPath, long videoLength, long presentationTimestampUs, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除动态照片声明，保留增益图等其它目录项。
    /// </summary>
    Task RemoveMotionPhotoAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把源图的元数据复制到转码后的封面；像素已按方向转正，方向固定写为 1。
    /// </summary>
    Task CopyCoverMetadataAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为照片写入 Apple MakerNotes 中的 ContentIdentifier。
    /// </summary>
    Task WriteApplePhotoIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为 QuickTime 视频写入配对标识、拍摄时间、位置与设备信息。
    /// </summary>
    Task WriteAppleVideoTagsAsync(string videoPath, AppleVideoTags tags, CancellationToken cancellationToken = default);
}
