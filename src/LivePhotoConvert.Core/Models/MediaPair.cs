namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 一组待合成的照片与视频
/// </summary>
/// <param name="PhotoPath">照片路径</param>
/// <param name="VideoPath">视频路径</param>
public sealed record MediaPair(string PhotoPath, string VideoPath)
{
    /// <summary>
    /// 用于匹配的文件名（不含扩展名）
    /// </summary>
    public string Name => Path.GetFileNameWithoutExtension(PhotoPath);
}

/// <summary>
/// 输入目录的匹配结果
/// </summary>
public sealed record PairingResult
{
    /// <summary>
    /// 匹配成功、可以参与合成的分组
    /// </summary>
    public required IReadOnlyList<MediaPair> Pairs { get; init; }

    /// <summary>
    /// 未匹配到视频的照片数量，这些文件不会被合成或清理
    /// </summary>
    public required int UnmatchedPhotoCount { get; init; }

    /// <summary>
    /// 未匹配到照片的视频数量（例如 iPhone 导出的长视频），这些文件不会被合成或清理
    /// </summary>
    public required int UnmatchedVideoCount { get; init; }

    /// <summary>
    /// 同名但因扩展名优先级较低而未参与合成的备选文件数量，这些文件同样不会被清理
    /// </summary>
    public required int SkippedDuplicateCount { get; init; }
}
