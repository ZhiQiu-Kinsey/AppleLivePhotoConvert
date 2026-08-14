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
    /// 待合成的候选分组，按文件名与扩展名优先级排序
    /// </summary>
    /// <remarks>
    /// 同名文件可能存在多个候选（例如同时存在 IMG_0001.heic、IMG_0001.jpg 与 IMG_0001.mov），
    /// 合成阶段会逐个校验并只取每组中第一个通过校验的候选。
    /// </remarks>
    public required IReadOnlyList<MediaPair> Pairs { get; init; }

    /// <summary>
    /// 未匹配到视频的照片数量，这些文件不会被合成或清理
    /// </summary>
    public required int UnmatchedPhotoCount { get; init; }

    /// <summary>
    /// 未匹配到照片的视频数量（例如 iPhone 导出的长视频），这些文件不会被合成或清理
    /// </summary>
    public required int UnmatchedVideoCount { get; init; }
}
