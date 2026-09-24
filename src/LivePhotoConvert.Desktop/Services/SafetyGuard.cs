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
            var full = Path.GetFullPath(targetDirectory);
            var root = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(full)
                : DeepestMountPoint(full, DriveInfo.GetDrives().Select(d => d.Name));
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
    /// Unix 的路径根总是 "/"，其它卷挂载在子目录上：取包含目标路径的最深挂载点，才能读到目标所在卷的剩余空间。
    /// </summary>
    internal static string? DeepestMountPoint(string fullPath, IEnumerable<string> mountPoints) =>
        mountPoints
            .Where(mount => mount == "/" || fullPath == mount.TrimEnd('/') || fullPath.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            .MaxBy(mount => mount.TrimEnd('/').Length);

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
