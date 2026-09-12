using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Desktop.Models;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 安卓动态照片内嵌微视频按需流式切片缓存服务：
/// 仅在需要播放（悬停微动/QuickLook）时将内嵌 MP4 切片到临时缓存目录，
/// 切片耗时仅 2~3ms，后续播放直接命中磁盘缓存，杜绝重复切片与全量解包。
/// </summary>
public static class MotionPhotoVideoCache
{
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", "motion_cache");
    private static readonly Lock Lock = new();

    /// <summary>
    /// 确保卡片对应的微视频已解压到临时可播放路径，并更新 card.VideoPath。
    /// </summary>
    /// <param name="card">目标照片卡片模型</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>可直接供 FFmpeg 或播放器读取的独立 MP4 文件路径；若非动态照片或提取失败则返回 null</returns>
    public static async Task<string?> EnsureVideoExtractedAsync(PhotoCardItemViewModel card, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(card.VideoPath) && File.Exists(card.VideoPath))
        {
            return card.VideoPath;
        }

        if (!card.IsMotionPhoto || card.EmbeddedVideoLength <= 0 || !File.Exists(card.PhotoPath))
        {
            return null;
        }

        Directory.CreateDirectory(CacheDirectory);

        string baseName = Path.GetFileNameWithoutExtension(card.PhotoPath);
        string cacheFileName = $"{baseName}_{card.EmbeddedVideoLength}_{card.EmbeddedVideoOffset}.mp4";
        string targetPath = Path.Combine(CacheDirectory, cacheFileName);

        // 检查磁盘缓存是否已存在且完整
        if (File.Exists(targetPath))
        {
            try
            {
                var fi = new FileInfo(targetPath);
                if (fi.Length == card.EmbeddedVideoLength)
                {
                    card.VideoPath = targetPath;
                    return targetPath;
                }
            }
            catch
            {
                // 文件可能被占用或损坏，重新切片
            }
        }

        // 执行流式分块无损切片（租借 ArrayPool 缓冲区，零堆垃圾）
        string tempWritingPath = Path.Combine(CacheDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await BinaryFile.CopySegmentAsync(
                card.PhotoPath,
                tempWritingPath,
                card.EmbeddedVideoOffset,
                card.EmbeddedVideoLength,
                cancellationToken);

            lock (Lock)
            {
                if (!File.Exists(targetPath))
                {
                    File.Move(tempWritingPath, targetPath);
                }
                else
                {
                    FileHelper.TryDeleteFile(tempWritingPath);
                }
            }

            card.VideoPath = targetPath;
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDeleteFile(tempWritingPath);
            throw;
        }
        catch
        {
            FileHelper.TryDeleteFile(tempWritingPath);
            return null;
        }
    }
}
