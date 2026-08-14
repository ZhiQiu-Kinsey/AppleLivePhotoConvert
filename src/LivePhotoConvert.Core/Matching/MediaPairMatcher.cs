using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 把照片与视频配对
/// </summary>
/// <remarks>
/// 优先按 ContentIdentifier 精确配对（同一张实况照片的照片与视频该值相同，且不受文件名影响），
/// 剩余无法按 CI 配对的再按文件名配对兜底。纯逻辑，不访问文件系统，便于测试。
/// </remarks>
public static class MediaPairMatcher
{
    /// <summary>
    /// 从文件列表中匹配出成对的照片与视频
    /// </summary>
    /// <param name="filePaths">待匹配的文件路径</param>
    /// <param name="photoContentIdentifiers">照片路径到 ContentIdentifier 的映射（可为空，表示不做 CI 配对）</param>
    /// <param name="videoContentIdentifiers">视频路径到 ContentIdentifier 的映射（可为空，表示不做 CI 配对）</param>
    /// <returns>匹配结果，含未匹配文件的统计</returns>
    public static PairingResult Match(
        IEnumerable<string> filePaths,
        IReadOnlyDictionary<string, string>? photoContentIdentifiers = null,
        IReadOnlyDictionary<string, string>? videoContentIdentifiers = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var files = filePaths.ToList();
        var photos = files.Where(MediaFileTypes.IsPhoto).ToList();
        var videos = files.Where(MediaFileTypes.IsVideo).ToList();

        var pairs = new List<MediaPair>();
        var usedPhotos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 第一步：ContentIdentifier 精确配对 ──
        // 同一张实况照片的照片与视频共享相同的 ContentIdentifier，
        // 因此即使 iCloud 下载导致文件名错位（照片 IMG_0011-1、视频 IMG_0011）也能正确配对。
        if (photoContentIdentifiers is not null && videoContentIdentifiers is not null)
        {
            var videosByCi = videos
                .Where(video => TryGetCi(videoContentIdentifiers, video, out _))
                .GroupBy(video => videoContentIdentifiers[video], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(GetVideoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
                    StringComparer.OrdinalIgnoreCase);

            var photosByCi = photos
                .Where(photo => TryGetCi(photoContentIdentifiers, photo, out _))
                .GroupBy(photo => photoContentIdentifiers[photo], StringComparer.OrdinalIgnoreCase);

            foreach (var photoGroup in photosByCi)
            {
                if (!videosByCi.TryGetValue(photoGroup.Key, out var video))
                {
                    continue;
                }

                var photo = photoGroup.OrderBy(GetPhotoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First();
                pairs.Add(new MediaPair(photo, video, IsContentIdentifierMatched: true));
                usedPhotos.Add(photo);
                usedVideos.Add(video);
            }
        }

        // ── 第二步：剩余文件按文件名配对（兜底）──
        var remainingPhotos = photos.Where(photo => !usedPhotos.Contains(photo));
        var remainingVideos = videos.Where(video => !usedVideos.Contains(video));
        var photosByName = remainingPhotos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase);
        var videosByName = remainingVideos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase)
                                         .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var photoGroup in photosByName)
        {
            if (!videosByName.TryGetValue(photoGroup.Key, out var sameNameVideos))
            {
                continue;
            }

            var orderedPhotos = photoGroup.OrderBy(GetPhotoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            var orderedVideos = sameNameVideos.OrderBy(GetVideoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var photo in orderedPhotos)
            {
                foreach (var video in orderedVideos)
                {
                    pairs.Add(new MediaPair(photo, video));
                }
            }
        }

        // 统计未匹配的文件（从未出现在任何配对里的照片/视频）
        var matchedPhotos = new HashSet<string>(pairs.Select(pair => pair.PhotoPath), StringComparer.OrdinalIgnoreCase);
        var matchedVideos = new HashSet<string>(pairs.Select(pair => pair.VideoPath), StringComparer.OrdinalIgnoreCase);
        return new PairingResult
        {
            Pairs = pairs,
            UnmatchedPhotoCount = photos.Count(photo => !matchedPhotos.Contains(photo)),
            UnmatchedVideoCount = videos.Count(video => !matchedVideos.Contains(video))
        };
    }

    /// <summary>
    /// 从映射中取出非空的 ContentIdentifier
    /// </summary>
    private static bool TryGetCi(IReadOnlyDictionary<string, string> map, string filePath, out string ci)
    {
        if (map.TryGetValue(filePath, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            ci = value;
            return true;
        }

        ci = string.Empty;
        return false;
    }

    /// <summary>
    /// 获取用于匹配的文件名（不含扩展名）
    /// </summary>
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
