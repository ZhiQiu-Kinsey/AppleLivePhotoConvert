namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 文件与目录安全操作的公共辅助类（统一异常防御与静默清理）
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// 尽力安全删除单个文件，发生任何异常时均静默忽略，避免清理失败反向影响主转换流程
    /// </summary>
    /// <param name="path">目标文件路径（支持为 null 或空字符串）</param>
    public static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败时静默忽略，不影响主流程
        }
    }

    /// <summary>
    /// 尽力递归删除整个目录，发生任何异常时均静默忽略
    /// </summary>
    /// <param name="path">目标目录路径（支持为 null 或空字符串）</param>
    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败时静默忽略，不影响主流程
        }
    }
}

/// <summary>
/// 文件时间戳同步帮助类，确保合成或拆分后的媒体在系统相册中查看时按拍摄时间准确排序
/// </summary>
public static class FileTimestamp
{
    /// <summary>
    /// 将源文件的时间戳（创建时间与最后修改时间）同步给一个或多个目标文件
    /// </summary>
    /// <param name="sourcePath">包含真实拍摄时间的源文件路径</param>
    /// <param name="targetPaths">需要同步时间戳的目标文件路径列表（支持以切片 Span 传入，无额外数组分配）</param>
    public static void Sync(string sourcePath, params ReadOnlySpan<string> targetPaths)
    {
        try
        {
            var creationTime = File.GetCreationTime(sourcePath);
            var lastWriteTime = File.GetLastWriteTime(sourcePath);

            foreach (var target in targetPaths)
            {
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.SetCreationTime(target, creationTime);
                    File.SetLastWriteTime(target, lastWriteTime);
                }
                catch
                {
                    // 忽略单个文件的时间设置异常（例如部分只读网络驱动器）
                }
            }
        }
        catch
        {
            // 部分虚拟文件系统不支持读取时间，不影响文件本身
        }
    }

    /// <summary>
    /// 从成对的照片与视频中取最早的时间戳同步给目标文件
    /// </summary>
    /// <remarks>
    /// 当用户从 iCloud、网盘或即时通讯工具下载照片时，常常出现图片修改时间被刷新为当前下载时间，
    /// 但伴随的 .MOV 视频依然保留了原始录制时间的情况。此时选举二者中更早的时间戳，能够最大程度还原拍摄现场时间。
    /// </remarks>
    /// <param name="targetPath">合成后的输出文件路径</param>
    /// <param name="photoPath">原照片文件路径</param>
    /// <param name="videoPath">原视频文件路径</param>
    public static void SyncEarliest(string targetPath, string photoPath, string videoPath)
    {
        try
        {
            var photoCreationTime = File.GetCreationTime(photoPath);
            var photoWriteTime = File.GetLastWriteTime(photoPath);
            var videoCreationTime = File.GetCreationTime(videoPath);
            var videoWriteTime = File.GetLastWriteTime(videoPath);

            var earliestCreation = photoCreationTime < videoCreationTime ? photoCreationTime : videoCreationTime;
            var earliestWrite = photoWriteTime < videoWriteTime ? photoWriteTime : videoWriteTime;

            File.SetCreationTime(targetPath, earliestCreation);
            File.SetLastWriteTime(targetPath, earliestWrite);
        }
        catch
        {
            // 忽略时间设置异常
        }
    }
}

