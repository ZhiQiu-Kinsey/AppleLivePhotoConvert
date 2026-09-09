namespace LivePhotoConvert.E2E.Harness;

/// <summary>
/// 桌面端安全防灾矩阵契约 (PRD Section 5.2):
/// 集中定义并严格校验容量预检 (SEC-03)、物理删除密码锁 (SEC-01)、
/// 就地覆盖原子备份 (SEC-02) 与孤儿临时目录自愈逻辑。
/// </summary>
public static class DesktopSafetyContract
{
    public const long SafetyReserveBytes = 500L * 1024 * 1024; // 500 MB
    public const double ExpansionFactor = 1.2;
    public const string DeletePassword = "DELETE";
    public const string BackupExtension = ".livephoto_backup";

    /// <summary>
    /// 计算批量处理所需最小可用磁盘空间: TotalSourceBytes * 1.2 + 500MB
    /// </summary>
    public static long CalculateRequiredDiskSpace(long totalSourceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSourceBytes);
        return (long)Math.Ceiling(totalSourceBytes * ExpansionFactor) + SafetyReserveBytes;
    }

    /// <summary>
    /// 预检磁盘空间是否足够
    /// </summary>
    public static PreflightDiskSpaceResult CheckPreflightDiskSpace(long totalSourceBytes, long availableFreeBytes)
    {
        var required = CalculateRequiredDiskSpace(totalSourceBytes);
        var isBlocked = availableFreeBytes < required;
        var shortfall = isBlocked ? (required - availableFreeBytes) : 0L;
        return new PreflightDiskSpaceResult(isBlocked, required, availableFreeBytes, shortfall);
    }

    /// <summary>
    /// 校验物理删除解锁密码：必须严格匹配全大写 "DELETE"，任何大小写变体或空格均视为无效
    /// </summary>
    public static bool VerifyDeletePassword(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        return string.Equals(input, DeletePassword, StringComparison.Ordinal);
    }

    /// <summary>
    /// 执行就地覆盖前的原子备份：将原文件重命名暂存为 .livephoto_backup
    /// </summary>
    public static string CreateInPlaceBackup(string targetFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        if (!File.Exists(targetFilePath))
        {
            throw new FileNotFoundException("Cannot backup non-existent file", targetFilePath);
        }

        var backupPath = targetFilePath + BackupExtension;
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Move(targetFilePath, backupPath);
        return backupPath;
    }

    /// <summary>
    /// 成功校验新产物后提交并清理暂存备份
    /// </summary>
    public static void CommitInPlaceBackup(string targetFilePath)
    {
        var backupPath = targetFilePath + BackupExtension;
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    /// <summary>
    /// 处理异常时从暂存备份安全回滚恢复原文件
    /// </summary>
    public static void RollbackInPlaceBackup(string targetFilePath)
    {
        var backupPath = targetFilePath + BackupExtension;
        if (File.Exists(backupPath))
        {
            if (File.Exists(targetFilePath))
            {
                File.Delete(targetFilePath);
            }
            File.Move(backupPath, targetFilePath);
        }
    }

    /// <summary>
    /// 自愈清理超过指定寿命（默认 24 小时）的孤儿临时目录
    /// </summary>
    public static int CleanOrphanTempDirectories(string tempRootPath, TimeSpan maxAge)
    {
        if (!Directory.Exists(tempRootPath))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - maxAge;
        var cleanedCount = 0;

        foreach (var dir in Directory.EnumerateDirectories(tempRootPath))
        {
            try
            {
                var creationTime = Directory.GetCreationTimeUtc(dir);
                if (creationTime < threshold)
                {
                    Directory.Delete(dir, recursive: true);
                    cleanedCount++;
                }
            }
            catch
            {
                // 忽略被占用目录
            }
        }

        return cleanedCount;
    }
}

public sealed record PreflightDiskSpaceResult(
    bool IsBlocked,
    long RequiredBytes,
    long AvailableFreeBytes,
    long ShortfallBytes
);
