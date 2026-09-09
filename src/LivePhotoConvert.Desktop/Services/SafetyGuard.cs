namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 银行级数据资产防灾安全矩阵
/// </summary>
public sealed class SafetyGuard
{
    /// <summary>
    /// SEC-03: 转换前磁盘剩余容量预检 (Pre-flight ENOSPC Check)
    /// 所需空间 = 总源文件大小 * 1.2 + 500MB
    /// </summary>
    public static (bool HasEnoughSpace, long RequiredBytes, long AvailableBytes) CheckDiskSpace(
        string targetDirectory, long totalSourceBytes)
    {
        long requiredBytes = (long)(totalSourceBytes * 1.2) + (500L * 1024 * 1024);
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(targetDirectory)) ?? "C:\\";
            DriveInfo drive = new(root);
            long availableBytes = drive.AvailableFreeSpace;
            return (availableBytes >= requiredBytes, requiredBytes, availableBytes);
        }
        catch
        {
            // 若获取驱动器信息异常，默认放行但返回预估需求
            return (true, requiredBytes, requiredBytes + 1024 * 1024);
        }
    }

    /// <summary>
    /// SEC-01: 校验物理删除解锁密码
    /// 强制要求键入完全匹配的大写 "DELETE" 文本
    /// </summary>
    public static bool ValidateDeletePassword(string? input)
        => string.Equals(input, "DELETE", StringComparison.Ordinal);

    /// <summary>
    /// SEC-04: 冷启动清理超期 24 小时的孤儿临时目录
    /// </summary>
    public static void CleanOrphanTempDirectories()
    {
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "LivePhotoConvert");
            if (!Directory.Exists(tempRoot)) return;

            DateTime threshold = DateTime.UtcNow.AddHours(-24);
            foreach (string dir in Directory.EnumerateDirectories(tempRoot, "temp-*"))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < threshold)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // 静默忽略被锁定的活跃目录
                }
            }
        }
        catch
        {
            // 防御性忽略临时目录根扫描异常
        }
    }

    /// <summary>
    /// SEC-05: 立即清理全部转换临时目录（不分年龄）。
    /// 供「偏好设置」AutoCleanTemp 开关在应用退出时驱动；
    /// 活跃目录被占用时静默跳过，绝不阻断退出流程。
    /// </summary>
    public static void CleanAllTempDirectories()
    {
        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "LivePhotoConvert");
            if (!Directory.Exists(tempRoot)) return;

            foreach (string dir in Directory.EnumerateDirectories(tempRoot, "temp-*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 静默忽略被锁定的活跃目录，下次启动由 CleanOrphanTempDirectories 兜底。
                }
            }
        }
        catch
        {
            // 防御性忽略临时目录根扫描异常
        }
    }

    /// <summary>
    /// SEC-02: 建立 .livephoto_backup 原片原子暂存备份
    /// </summary>
    public static string CreateAtomicBackup(string sourceFilePath)
    {
        string backupPath = sourceFilePath + ".livephoto_backup";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
        File.Copy(sourceFilePath, backupPath);
        return backupPath;
    }

    /// <summary>
    /// SEC-02: 成功后清理暂存备份
    /// </summary>
    public static void DeleteAtomicBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            // 忽略延迟删除异常
        }
    }

    /// <summary>
    /// SEC-02: 发生异常时恢复暂存备份
    /// </summary>
    public static void RestoreAtomicBackup(string backupPath, string originalPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, originalPath, overwrite: true);
                File.Delete(backupPath);
            }
        }
        catch
        {
            // 尽最大努力恢复
        }
    }
}
