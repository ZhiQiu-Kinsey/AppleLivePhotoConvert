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

        var photosByName = photos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase);
        var videosByName = videos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // 同名文件可能存在多个格式（如 IMG_0001.heic 与 IMG_0001.jpg），
        // 全部保留为候选并按扩展名优先级排序，合成阶段会校验并只取每组第一个通过者。
        // 这样既能优先取画质更好的格式，又能在同名不同内容的照片里选对与视频真正匹配的那张。
        var pairs = new List<MediaPair>();
        foreach (var photoGroup in photosByName)
        {
            if (!videosByName.TryGetValue(photoGroup.Key, out var sameNameVideos))
            {
                continue;
            }

            var orderedPhotos = photoGroup.OrderBy(GetPhotoRank).ThenBy(filePath => filePath, StringComparer.OrdinalIgnoreCase);
            var orderedVideos = sameNameVideos.OrderBy(GetVideoRank).ThenBy(filePath => filePath, StringComparer.OrdinalIgnoreCase);
            foreach (var photo in orderedPhotos)
            {
                foreach (var video in orderedVideos)
                {
                    pairs.Add(new MediaPair(photo, video));
                }
            }
        }

        // 统计未匹配的文件，它们在任何清理选项下都不会被处理
        var matchedNames = new HashSet<string>(pairs.Select(pair => pair.Name), StringComparer.OrdinalIgnoreCase);
        return new PairingResult
        {
            Pairs = pairs,
            UnmatchedPhotoCount = photos.Count(filePath => !matchedNames.Contains(GetNameKey(filePath))),
            UnmatchedVideoCount = videos.Count(filePath => !matchedNames.Contains(GetNameKey(filePath)))
        };
    }

    /// <summary>
    /// 获取用于匹配的文件名（不含扩展名）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>不含扩展名的文件名</returns>
    private static string GetNameKey(string filePath) => Path.GetFileNameWithoutExtension(filePath);

    /// <summary>
    /// 照片扩展名优先级
    /// </summary>
    private static int GetPhotoRank(string filePath) => GetExtensionRank(filePath, MediaFileTypes.PhotoExtensionPriority);

    /// <summary>
    /// 视频扩展名优先级
    /// </summary>
    private static int GetVideoRank(string filePath) => GetExtensionRank(filePath, MediaFileTypes.VideoExtensionPriority);

    /// <summary>
    /// 扩展名在优先级列表中的排名，越靠前排名越小
    /// </summary>
    private static int GetExtensionRank(string filePath, string[] extensionPriority)
    {
        var rank = Array.FindIndex(extensionPriority, extension => extension.Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase));
        return rank < 0 ? extensionPriority.Length : rank;
    }
}
