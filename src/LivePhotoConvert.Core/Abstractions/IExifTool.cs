namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// Content Identifier 所在的元数据分组，照片与视频的标签组不同
/// </summary>
public enum ContentIdentifierKind
{
    /// <summary>
    /// 照片，读取 Apple 分组
    /// </summary>
    Photo,

    /// <summary>
    /// 视频，读取 Keys 分组
    /// </summary>
    Video
}

/// <summary>
/// ExifTool 元数据读写
/// </summary>
public interface IExifTool : IAsyncDisposable
{
    /// <summary>
    /// 写入动态照片标记，使相册能识别文件中附带的视频
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="videoOffset">视频数据的字节长度，即从文件末尾回溯的偏移量</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入动态照片标记，并指定封面图片在视频中对应帧的真实时间戳
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="videoOffset">视频数据的字节长度，即从文件末尾回溯的偏移量</param>
    /// <param name="presentationTimestampUs">封面图片对应视频帧的时间戳（微秒）；无法确定时传 0</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task WriteMotionPhotoTagsAsync(string imagePath, long videoOffset, long presentationTimestampUs, CancellationToken cancellationToken = default) =>
        WriteMotionPhotoTagsAsync(imagePath, videoOffset, cancellationToken);

    /// <summary>
    /// 从 Apple Live Photo 视频的 StillImageTime 定时元数据轨道读取封面帧时间戳
    /// </summary>
    /// <param name="videoPath">Apple Live Photo 的 MOV 视频路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>封面帧时间戳（微秒）；不存在或无法解析时返回 <c>null</c></returns>
    Task<long?> TryReadAppleLivePhotoPresentationTimestampUsAsync(string videoPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);

    /// <summary>
    /// 清除动态照片标记，用于拆分后还原成普通图片
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RemoveMotionPhotoTagsAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将源文件的全部元数据（EXIF、GPS、MakerNotes、XMP、ICC 颜色配置文件等）完整复制到目标文件
    /// </summary>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="destinationPath">目标文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task CopyAllTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将源图片的元数据（GPS、MakerNotes、ICC 配置文件等）复制到转码后的封面 JPEG，
    /// 同时排除旧的 Orientation 与像素宽高标签，并将 Orientation 显式重置为 1（正常不旋转），避免相册产生二次旋转颠倒
    /// </summary>
    /// <param name="sourcePath">源图片路径</param>
    /// <param name="destinationPath">目标封面路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task CopyCoverTagsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取 MicroVideoOffset 标签
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>偏移量；文件不是动态照片时返回 <c>null</c></returns>
    Task<long?> TryReadMicroVideoOffsetAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取苹果的 Content Identifier，同一张实况照片的照片与视频该值相同
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="kind">文件类型，决定读取哪个标签组</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>标识值；文件不含该标签时返回 <c>null</c></returns>
    Task<string?> TryReadContentIdentifierAsync(string filePath, ContentIdentifierKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为照片写入 Apple Live Photo 唯一标识 (ContentIdentifier)
    /// </summary>
    /// <param name="photoPath">照片文件路径</param>
    /// <param name="contentIdentifier">全局唯一标识符</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task WriteAppleContentIdentifierAsync(string photoPath, string contentIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// 为 QuickTime 视频写入 Apple Live Photo 唯一标识 (ContentIdentifier) 并从照片同步拍摄时间与设备元数据
    /// </summary>
    /// <param name="videoPath">QuickTime 视频文件路径</param>
    /// <param name="photoPath">配对的照片文件路径（可选，用于同步拍摄时间、GPS 和相机信息）</param>
    /// <param name="contentIdentifier">全局唯一标识符</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task WriteAppleVideoMetadataAsync(string videoPath, string? photoPath, string contentIdentifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取文件的拍摄/创建时间
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>拍摄时间；无法读取时返回 <c>null</c></returns>
    Task<DateTime?> TryReadCreateDateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取视频时长
    /// </summary>
    /// <param name="filePath">视频文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>视频时长；无法读取时返回 <c>null</c></returns>
    Task<TimeSpan?> TryReadDurationAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断视频是否带镜像变换矩阵（iPhone 前置摄像头视频的典型特征）
    /// </summary>
    /// <remarks>
    /// 前置摄像头视频的 QuickTime 变换矩阵行列式为负（含镜像），
    /// 安卓相册等 MP4 播放器不识别镜像矩阵，会导致内嵌视频方向颠倒，
    /// 因此需要检测出来改走重新编码把方向烧进像素。
    /// </remarks>
    /// <param name="videoPath">视频文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否含镜像矩阵；无法读取时返回 <c>false</c>（按无需镜像处理）</returns>
    Task<bool> IsMirroredVideoAsync(string videoPath, CancellationToken cancellationToken = default);
}
