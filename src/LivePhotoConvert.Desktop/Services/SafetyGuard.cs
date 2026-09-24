using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 执行转换前后的安全检查与临时文件清理。
/// </summary>
public static class SafetyGuard
{
    private const long SpaceReserveBytes = 500L * 1024 * 1024;

    /// <summary>
    /// 输出盘剩余空间是否足够：源文件总量的 1.2 倍再加 500MB 余量（转码与暂存文件同时存在）。
    /// 无法读取磁盘信息时放行。
    /// </summary>
    public static (bool HasEnoughSpace, long RequiredBytes, long AvailableBytes) CheckDiskSpace(string targetDirectory, long totalSourceBytes)
    {
        var required = (long)(totalSourceBytes * 1.2) + SpaceReserveBytes;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetDirectory));
            if (string.IsNullOrEmpty(root))
            {
                return (true, required, required);
            }

            var available = new DriveInfo(root).AvailableFreeSpace;
            return (available >= required, required, available);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (true, required, required);
        }
    }

    /// <summary>
    /// 永久删除原片前要求用户输入大写 DELETE。
    /// </summary>
    public static bool ValidateDeletePassword(string? input) => string.Equals(input, "DELETE", StringComparison.Ordinal);

    /// <summary>
    /// 启动时清理上次异常退出遗留超过 24 小时的工作目录。
    /// </summary>
    public static void CleanOrphanTempDirectories() => TempWorkspace.DeleteOrphans(TimeSpan.FromHours(24));

    /// <summary>
    /// 退出时只清理本进程创建的工作目录，其它正在运行的实例的目录不受影响；被占用的目录由下次启动兜底。
    /// </summary>
    public static void CleanOwnTempDirectories() => TempWorkspace.DeleteOwned();
}
