using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Desktop.Models;
namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 旧播放器只能读独立文件：播放安卓动态照片前把内嵌视频无损切到临时目录，之后直接命中。
/// 切出的文件只交给播放器，不写回卡片（阶段 3 的播放器改为直接读取照片内的区段后删除本类）。
/// </summary>
public static class MotionPhotoVideoCache
{
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert", "motion_cache");
    private static readonly Lock Lock = new();

    /// <returns>可供播放器读取的 MP4 路径；不是动态照片或切片失败时为 null</returns>
    public static async Task<string?> EnsureVideoExtractedAsync(PhotoCardItemViewModel card, CancellationToken cancellationToken = default)
    {
        if (card.Video is not { IsEmbedded: true, Length: > 0 } video || !File.Exists(video.Path))
        {
            return null;
        }

        Directory.CreateDirectory(CacheDirectory);
        var targetPath = Path.Combine(CacheDirectory, $"{Path.GetFileNameWithoutExtension(video.Path)}_{video.Length}_{video.Offset}.mp4");
        try
        {
            if (new FileInfo(targetPath) is { Exists: true } existing && existing.Length == video.Length)
            {
                return targetPath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被占用或损坏时重新切片
        }

        var tempWritingPath = Path.Combine(CacheDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await BinaryFile.CopySegmentAsync(video.Path, tempWritingPath, video.Offset, video.Length, cancellationToken);
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

            return targetPath;
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDeleteFile(tempWritingPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            FileHelper.TryDeleteFile(tempWritingPath);
            return null;
        }
    }
}
