using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 把照片与视频配成候选对。纯内存计算，不访问磁盘。
/// </summary>
/// <remarks>
/// 先按 Apple ContentIdentifier 精确配对（文件名被网盘改过也能配上），剩余文件再按「同一目录 + 同一文件名主干」配对。
/// 同名候选会按扩展名优先级全部列出，由合成阶段逐个校验后择优。
/// </remarks>
public static class MediaPairMatcher
{
    /// <param name="filePaths">待配对的文件</param>
    /// <param name="contentIdentifiers">文件路径到 ContentIdentifier 的映射；为 <c>null</c> 时跳过精确配对</param>
    public static PairingResult Match(IEnumerable<string> filePaths, IReadOnlyDictionary<string, string>? contentIdentifiers = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var photos = new List<string>();
        var videos = new List<string>();
        foreach (var file in filePaths)
        {
            if (MediaFileTypes.IsPhoto(file))
            {
                photos.Add(file);
            }
            else if (MediaFileTypes.IsVideo(file))
            {
                videos.Add(file);
            }
        }

        var pairs = new List<MediaPair>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (contentIdentifiers is not null)
        {
            MatchByContentIdentifier(photos, videos, contentIdentifiers, pairs, used);
        }

        var videosByKey = videos.Where(video => !used.Contains(video))
                                .GroupBy(MediaPair.GroupKeyOf, KeyComparer)
                                .ToDictionary(group => group.Key, group => ByVideoRank(group).ToList(), KeyComparer);

        foreach (var photoGroup in photos.Where(photo => !used.Contains(photo)).GroupBy(MediaPair.GroupKeyOf, KeyComparer))
        {
            if (videosByKey.TryGetValue(photoGroup.Key, out var sameNameVideos))
            {
                pairs.AddRange(from photo in ByPhotoRank(photoGroup) from video in sameNameVideos select new MediaPair(photo, video));
            }
        }

        // 同一 ContentIdentifier 下被择优淘汰的其它格式也算已归属，不列为孤立文件
        var pairedPhotos = pairs.Select(pair => pair.PhotoPath).Concat(used).ToHashSet(StringComparer.Ordinal);
        var pairedVideos = pairs.Select(pair => pair.VideoPath).Concat(used).ToHashSet(StringComparer.Ordinal);
        return new PairingResult
        {
            Pairs = pairs,
            PhotosWithoutVideo = [.. photos.Where(photo => !pairedPhotos.Contains(photo))],
            VideosWithoutPhoto = [.. videos.Where(video => !pairedVideos.Contains(video))]
        };
    }

    /// <summary>
    /// 同一 ContentIdentifier 下取画质最高的照片与优先级最高的视频；同标识的其它格式（如同时导出的 JPG）一并视为已使用，
    /// 避免在同名配对阶段被重复配对。
    /// </summary>
    private static void MatchByContentIdentifier(List<string> photos, List<string> videos, IReadOnlyDictionary<string, string> identifiers, List<MediaPair> pairs, HashSet<string> used)
    {
        var videosById = videos.Where(video => HasIdentifier(identifiers, video))
                               .GroupBy(video => identifiers[video], StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(group => group.Key, group => ByVideoRank(group).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var photoGroup in photos.Where(photo => HasIdentifier(identifiers, photo)).GroupBy(photo => identifiers[photo], StringComparer.OrdinalIgnoreCase))
        {
            if (!videosById.TryGetValue(photoGroup.Key, out var candidates))
            {
                continue;
            }

            var photo = ByPhotoRank(photoGroup).First();
            pairs.Add(new MediaPair(photo, candidates[0], IsContentIdentifierMatched: true));
            used.UnionWith(photoGroup);
            used.UnionWith(candidates);
        }
    }

    private static bool HasIdentifier(IReadOnlyDictionary<string, string> identifiers, string path) =>
        identifiers.TryGetValue(path, out var value) && !string.IsNullOrWhiteSpace(value);

    private static IOrderedEnumerable<string> ByPhotoRank(IEnumerable<string> paths) =>
        paths.OrderBy(MediaFileTypes.PhotoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static IOrderedEnumerable<string> ByVideoRank(IEnumerable<string> paths) =>
        paths.OrderBy(MediaFileTypes.VideoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;
}
