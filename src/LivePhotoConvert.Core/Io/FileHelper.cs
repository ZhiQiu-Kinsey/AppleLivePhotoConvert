namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 尽力而为的清理操作：清理失败不应掩盖主流程的结果。
/// </summary>
public static class FileHelper
{
    public static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被占用或无权限时保留，由孤儿清理兜底
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被占用或无权限时保留，由孤儿清理兜底
        }
    }
}

/// <summary>
/// 文件系统时间戳。相册按文件时间排序，输出文件应沿用原片的时间。
/// </summary>
public readonly record struct FileTimestamp(DateTime CreationTimeUtc, DateTime LastWriteTimeUtc)
{
    public static FileTimestamp Read(string path) => new(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path));

    /// <summary>
    /// 取多个源文件中最早的时间：从网盘或聊天工具下载的照片修改时间常被刷新为下载时间，而配对视频仍保留原始时间。
    /// </summary>
    public static FileTimestamp Earliest(params ReadOnlySpan<string> paths)
    {
        var creation = DateTime.MaxValue;
        var lastWrite = DateTime.MaxValue;
        foreach (var path in paths)
        {
            var timestamp = Read(path);
            creation = timestamp.CreationTimeUtc < creation ? timestamp.CreationTimeUtc : creation;
            lastWrite = timestamp.LastWriteTimeUtc < lastWrite ? timestamp.LastWriteTimeUtc : lastWrite;
        }

        return new FileTimestamp(creation, lastWrite);
    }

    /// <summary>
    /// 应用到目标文件；部分网络驱动器不支持修改时间，失败时忽略。
    /// </summary>
    public void ApplyTo(params ReadOnlySpan<string> targets)
    {
        foreach (var target in targets)
        {
            try
            {
                File.SetCreationTimeUtc(target, CreationTimeUtc);
                File.SetLastWriteTimeUtc(target, LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 时间戳只影响排序，不影响文件内容
            }
        }
    }
}
