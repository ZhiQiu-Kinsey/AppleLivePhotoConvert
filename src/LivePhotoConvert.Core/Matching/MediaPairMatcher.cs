using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 照片与视频的智能配对引擎（两阶段匹配：Apple ContentIdentifier 唯一标识精准匹配 + 同名优先级兜底）
/// </summary>
/// <remarks>
/// 1. 阶段一：优先按 Apple 媒体元数据中的 ContentIdentifier（CI）进行 1:1 精确配对（即便文件名因 iCloud 传输错位也能准确关联）；<br/>
/// 2. 阶段二：对剩余未匹配文件按纯文件名（不含扩展名）进行兜底配对；<br/>
/// 3. 本类为纯逻辑运算，不直接访问磁盘 I/O，具备极高执行性能且易于测试。
/// </remarks>
public static class MediaPairMatcher
{
    /// <summary>
    /// 从指定的文件路径列表中匹配出成对的照片与视频
    /// </summary>
    /// <param name="filePaths">待匹配的文件全路径列表</param>
    /// <param name="photoContentIdentifiers">照片路径到 ContentIdentifier 的映射表（可为 null，表示跳过 CI 阶段）</param>
    /// <param name="videoContentIdentifiers">视频路径到 ContentIdentifier 的映射表（可为 null，表示跳过 CI 阶段）</param>
    /// <returns>包含成功配对列表与未匹配统计的 <see cref="PairingResult"/></returns>
    public static PairingResult Match(IEnumerable<string> filePaths,IReadOnlyDictionary<string, string>? photoContentIdentifiers = null,IReadOnlyDictionary<string, string>? videoContentIdentifiers = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // 单趟循环快速将文件列表划分为照片候选与视频候选
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
        var usedPhotos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 第一阶段：Apple ContentIdentifier (CI) 精确配对 ──
        // 同一张实况照片的照片（HEIC/JPG）与视频（MOV）在 EXIF/QuickTime 元数据中共享相同的 ContentIdentifier。
        // 即便 iCloud 下载或跨平台传输导致文件名重命名（如照片为 IMG_0011-1.heic、视频为 IMG_0011.mov），依然能 100% 准确配对。
        if (photoContentIdentifiers is not null && videoContentIdentifiers is not null)
        {
            var videosByCi = videos.Where(video => TryGetCi(videoContentIdentifiers, video))
                                   .GroupBy(video => videoContentIdentifiers[video], StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(group => group.Key,
                                       group => group.OrderBy(GetVideoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                                       StringComparer.OrdinalIgnoreCase);

            var photosByCi = photos.Where(photo => TryGetCi(photoContentIdentifiers, photo))
                                   .GroupBy(photo => photoContentIdentifiers[photo], StringComparer.OrdinalIgnoreCase);

            foreach (var photoGroup in photosByCi)
            {
                if (!videosByCi.TryGetValue(photoGroup.Key, out var videoCandidates) || videoCandidates.Count == 0)
                {
                    continue;
                }

                // 取画质最高的一张照片（如 HEIC 优于 JPG）与优先级最高的视频配对
                var photo = photoGroup.OrderBy(GetPhotoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First();
                var video = videoCandidates.First();
                pairs.Add(new MediaPair(photo, video, IsContentIdentifierMatched: true));

                // 核心排他逻辑：该 CI 下的所有多余格式（如同时导出的低画质同名 JPG）均已归属此实况照片，
                // 全部标记为已使用，防止在第二阶段兜底时被二次误匹配。
                foreach (var p in photoGroup)
                {
                    usedPhotos.Add(p);
                }

                foreach (var v in videoCandidates)
                {
                    usedVideos.Add(v);
                }
            }
        }

        // ── 第二阶段：剩余未配对文件按文件名（不含扩展名）进行兜底配对 ──
        var remainingPhotos = photos.Where(photo => !usedPhotos.Contains(photo));
        var remainingVideos = videos.Where(video => !usedVideos.Contains(video));
        var photosByName = remainingPhotos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase);
        var videosByName = remainingVideos.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var photoGroup in photosByName)
        {
            if (!videosByName.TryGetValue(photoGroup.Key, out var sameNameVideos))
            {
                continue;
            }

            var orderedPhotos = photoGroup.OrderBy(GetPhotoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            var orderedVideos = sameNameVideos.OrderBy(GetVideoRank).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            pairs.AddRange(from photo in orderedPhotos from video in orderedVideos select new MediaPair(photo, video));
        }

        // 统计未被任何配对采纳的孤立照片与视频数量
        var matchedPhotos = new HashSet<string>(pairs.Select(pair => pair.PhotoPath), StringComparer.OrdinalIgnoreCase);
        var matchedVideos = new HashSet<string>(pairs.Select(pair => pair.VideoPath), StringComparer.OrdinalIgnoreCase);
        return new PairingResult
        {
            Pairs = pairs,
            UnmatchedPhotoCount = photos.Count(photo => !matchedPhotos.Contains(photo)),
            UnmatchedVideoCount = videos.Count(video => !matchedVideos.Contains(video)),
            PhotoContentIdentifiers = photoContentIdentifiers,
            VideoContentIdentifiers = videoContentIdentifiers
        };
    }

    /// <summary>
    /// 从映射字典中安全提取非空白的 ContentIdentifier 字符串
    /// </summary>
    private static bool TryGetCi(IReadOnlyDictionary<string, string> map, string filePath)
    {
        return map.TryGetValue(filePath, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// 获取用于同名配对的基础文件名（去除扩展名）
    /// </summary>
    private static string GetNameKey(string filePath) => Path.GetFileNameWithoutExtension(filePath);

    /// <summary>
    /// 获取照片扩展名的优先级排名（基于 FrozenDictionary O(1) 检索，值越小优先级越高）
    /// </summary>
    private static int GetPhotoRank(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return MediaFileTypes.PhotoExtensionRanks.GetValueOrDefault(ext, int.MaxValue);
    }

    /// <summary>
    /// 获取视频扩展名的优先级排名（基于 FrozenDictionary O(1) 检索，值越小优先级越高）
    /// </summary>
    private static int GetVideoRank(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return MediaFileTypes.VideoExtensionRanks.GetValueOrDefault(ext, int.MaxValue);
    }
}
