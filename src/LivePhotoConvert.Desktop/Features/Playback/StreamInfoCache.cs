using System.Collections.Concurrent;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 视频流信息缓存：来回悬浮同一张卡片时不必每次都启动 FFmpeg 探测。
/// </summary>
/// <remarks>键含文件大小与修改时间，文件被替换后自动失效；探测失败不缓存。</remarks>
internal static class StreamInfoCache
{
    private const int MaxEntries = 256;

    private static readonly ConcurrentDictionary<(string Path, long Offset, long Length, bool Embedded, long FileLength, DateTime LastWrite), VideoStreamInfo> Entries = new();

    public static async Task<VideoStreamInfo?> GetAsync(string ffmpegPath, VideoSource source, CancellationToken cancellationToken)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists)
        {
            return null;
        }

        var key = (file.FullName, source.Offset, source.Length, source.IsEmbedded, file.Length, file.LastWriteTimeUtc);
        if (Entries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = await VideoStreamProbe.ProbeAsync(ffmpegPath, source.ToFfmpegInput(), cancellationToken: cancellationToken);
        if (info is not null)
        {
            if (Entries.Count >= MaxEntries)
            {
                Entries.Clear();
            }

            Entries[key] = info;
        }

        return info;
    }
}
