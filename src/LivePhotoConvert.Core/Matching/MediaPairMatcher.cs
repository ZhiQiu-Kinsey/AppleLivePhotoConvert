using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 按文件名把照片和视频配对
/// </summary>
/// <remarks>纯逻辑，不访问文件系统，便于测试。</remarks>
public static class MediaPairMatcher
{
    /// <summary>
    /// 从文件列表中匹配出成对的照片与视频
    /// </summary>
    /// <param name="filePaths">待匹配的文件路径</param>
    /// <returns>匹配结果，含未匹配文件的统计</returns>
    public static PairingResult Match(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var files = filePaths.ToList();
        var photos = files.Where(MediaFileTypes.IsPhoto).ToList();
        var videos = files.Where(MediaFileTypes.IsVideo).ToList();
        // 同名多格式（如 IMG_0001.heic 与 IMG_0001.jpg）只保留优先级最高的一个，避免同一张照片被匹配成多组
        var uniquePhotos = PickPreferredByName(photos, MediaFileTypes.PhotoExtensionPriority);
        var uniqueVideos = PickPreferredByName(videos, MediaFileTypes.VideoExtensionPriority);
        var pairs = uniquePhotos.Join(uniqueVideos, GetNameKey, GetNameKey, (photoPath, videoPath) => new MediaPair(photoPath, videoPath), StringComparer.OrdinalIgnoreCase).ToList();
        // 统计未匹配的文件，它们在任何清理选项下都不会被处理
        var matchedNames = new HashSet<string>(pairs.Select(pair => GetNameKey(pair.PhotoPath)), StringComparer.OrdinalIgnoreCase);
        var matchedFiles = new HashSet<string>(pairs.SelectMany(pair => new[] { pair.PhotoPath, pair.VideoPath }), StringComparer.OrdinalIgnoreCase);
        return new PairingResult
        {
            Pairs = pairs,
            UnmatchedPhotoCount = photos.Count(f => !matchedNames.Contains(GetNameKey(f))),
            UnmatchedVideoCount = videos.Count(f => !matchedNames.Contains(GetNameKey(f))),
            // 同名但未被选中的备选格式，只清理实际参与合成的那个文件，这些一律保留
            SkippedDuplicateCount = photos.Concat(videos).Count(f => matchedNames.Contains(GetNameKey(f)) && !matchedFiles.Contains(f))
        };
    }

    /// <summary>
    /// 获取用于匹配的文件名（不含扩展名）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>不含扩展名的文件名</returns>
    private static string GetNameKey(string filePath) => Path.GetFileNameWithoutExtension(filePath);

    /// <summary>
    /// 同名文件按扩展名优先级只保留一个
    /// </summary>
    /// <param name="files">文件列表</param>
    /// <param name="extensionPriority">扩展名优先级，越靠前优先级越高</param>
    /// <returns>去重后的文件列表</returns>
    private static List<string> PickPreferredByName(List<string> files, string[] extensionPriority)
    {
        return files.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(GetExtensionRank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).First())
                    .ToList();

        int GetExtensionRank(string filePath)
        {
            var rank = Array.FindIndex(extensionPriority, e => e.Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase));
            return rank < 0 ? extensionPriority.Length : rank;
        }
    }
}
