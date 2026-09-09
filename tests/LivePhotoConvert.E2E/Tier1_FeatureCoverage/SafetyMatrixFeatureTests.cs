using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1: 桌面端安全防灾矩阵特性覆盖测试 (Safety Matrix Feature Tests)
/// 严格验证 PRD 5.2 节所规定的四大防灾支柱：
/// 1. 磁盘容量预检阻断 (SEC-03)
/// 2. 物理删除原片大写 DELETE 文本密码锁 (SEC-01)
/// 3. 就地覆盖 .livephoto_backup 原子暂存与回滚机制 (SEC-02)
/// 4. 超过 24 小时孤儿临时目录自愈清理
/// </summary>
public class SafetyMatrixFeatureTests
{
    [Fact]
    public void PreflightDiskSpace_WhenInsufficient_BlocksAndCalculatesShortfall()
    {
        var totalSourceBytes = 100L * 1024 * 1024; // 100 MB
        // 需空间: 100MB * 1.2 + 500MB = 620MB
        var required = DesktopSafetyContract.CalculateRequiredDiskSpace(totalSourceBytes);
        Assert.Equal(620L * 1024 * 1024, required);

        // 仅提供 400 MB 可用空间
        var available = 400L * 1024 * 1024;
        var result = DesktopSafetyContract.CheckPreflightDiskSpace(totalSourceBytes, available);

        Assert.True(result.IsBlocked);
        Assert.Equal(required, result.RequiredBytes);
        Assert.Equal(available, result.AvailableFreeBytes);
        Assert.Equal(220L * 1024 * 1024, result.ShortfallBytes);
    }

    [Fact]
    public void PreflightDiskSpace_WhenSufficient_AllowsExecution()
    {
        var totalSourceBytes = 50L * 1024 * 1024; // 50 MB
        // 需空间: 50MB * 1.2 + 500MB = 560MB
        var available = 1000L * 1024 * 1024; // 1000 MB
        var result = DesktopSafetyContract.CheckPreflightDiskSpace(totalSourceBytes, available);

        Assert.False(result.IsBlocked);
        Assert.Equal(0, result.ShortfallBytes);
    }

    [Theory]
    [InlineData("DELETE", true)]
    [InlineData("delete", false)]
    [InlineData("Delete", false)]
    [InlineData("DELETE ", false)]
    [InlineData(" DELETE", false)]
    [InlineData("DEL", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DeletePasswordLock_OnlyAcceptsExactUppercaseDelete(string? input, bool expectedValid)
    {
        var isValid = DesktopSafetyContract.VerifyDeletePassword(input);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void InPlaceAtomicBackup_Lifecycle_SupportsBackupCommitAndRollback()
    {
        using var context = new E2ETestContext();
        var originalPath = context.CreateInputFile("IMPORTANT_LIVE.jpg", [0xAA, 0xBB, 0xCC]);

        // 1. 创建原子暂存备份
        var backupPath = DesktopSafetyContract.CreateInPlaceBackup(originalPath);
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(backupPath));
        Assert.Equal([0xAA, 0xBB, 0xCC], File.ReadAllBytes(backupPath));

        // 2. 模拟处理失败，执行回滚
        DesktopSafetyContract.RollbackInPlaceBackup(originalPath);
        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(backupPath));
        Assert.Equal([0xAA, 0xBB, 0xCC], File.ReadAllBytes(originalPath));

        // 3. 再次备份并在成功后提交
        DesktopSafetyContract.CreateInPlaceBackup(originalPath);
        File.WriteAllBytes(originalPath, [0x01, 0x02]); // 写入新产物
        DesktopSafetyContract.CommitInPlaceBackup(originalPath);

        Assert.True(File.Exists(originalPath));
        Assert.False(File.Exists(backupPath));
        Assert.Equal([0x01, 0x02], File.ReadAllBytes(originalPath));
    }

    [Fact]
    public void OrphanTempDirectories_CleansDirectoriesOlderThan24Hours()
    {
        using var context = new E2ETestContext();
        var oldDir = Path.Combine(context.TempDirectory, "strip-old-orphan");
        var newDir = Path.Combine(context.TempDirectory, "strip-fresh-active");

        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);

        // 设置旧目录创建时间为 30 小时前
        Directory.SetCreationTimeUtc(oldDir, DateTime.UtcNow.AddHours(-30));
        // 设置新目录创建时间为 10 分钟前
        Directory.SetCreationTimeUtc(newDir, DateTime.UtcNow.AddMinutes(-10));

        var cleaned = DesktopSafetyContract.CleanOrphanTempDirectories(context.TempDirectory, TimeSpan.FromHours(24));

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
    }
}
