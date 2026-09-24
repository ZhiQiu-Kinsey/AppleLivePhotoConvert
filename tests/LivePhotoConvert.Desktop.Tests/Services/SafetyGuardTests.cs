using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Tests.Services;

/// <summary>磁盘空间预留计算与永久删除口令校验。</summary>
public class SafetyGuardTests
{
    private const long Reserve = 500L * 1024 * 1024;

    [Fact]
    public void CheckDiskSpace_ZeroBytes_RequiresOnlyReserve()
    {
        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", 0);

        Assert.Equal(Reserve, requiredBytes);
        Assert.True(availableBytes >= 0);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_OneByte_TruncatesMarginToWholeBytes()
    {
        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", 1);

        Assert.Equal(1 + Reserve, requiredBytes);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_100GB_CalculatesWithoutPrecisionLoss()
    {
        const long oneGB = 1024L * 1024 * 1024;

        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", 100L * oneGB);

        // 100GB × 1.2 + 500MB
        Assert.Equal(129_373_306_880L, requiredBytes);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_InvalidDirectory_DoesNotBlockTheTask()
    {
        const long source = 10L * 1024 * 1024;

        var result = SafetyGuard.CheckDiskSpace("::InvalidDrive:\\path", source);

        // 查询不到卷信息时不能因为误判空间不足而拦下任务
        Assert.Equal((long)(source * 1.2) + Reserve, result.RequiredBytes);
        Assert.True(result.HasEnoughSpace);
    }

    [Fact]
    public void ValidateDeletePassword_ExactUppercase_Accepted()
    {
        Assert.True(SafetyGuard.ValidateDeletePassword("DELETE"));
    }

    [Theory]
    [InlineData("DELETE ")]
    [InlineData(" DELETE")]
    [InlineData(" DELETE ")]
    [InlineData("  DELETE  ")]
    [InlineData("   DELETE   ")]
    [InlineData("\tDELETE")]
    [InlineData("DELETE\t")]
    [InlineData("\tDELETE\t")]
    [InlineData("\r\nDELETE")]
    [InlineData("DELETE\r\n")]
    [InlineData("\r\nDELETE\r\n")]
    [InlineData("\nDELETE\n")]
    [InlineData("DEL ETE")]
    [InlineData("D E L E T E")]
    public void ValidateDeletePassword_AnyWhitespace_Rejected(string input)
    {
        // 严格序号比较：首尾或内部空白都视为误触
        Assert.False(SafetyGuard.ValidateDeletePassword(input));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("Delete")]
    [InlineData("DeLeTe")]
    [InlineData("dELETE")]
    [InlineData("deletE")]
    [InlineData("DEL")]
    [InlineData("DELETE1")]
    [InlineData("DELETE_NOW")]
    [InlineData("DELET")]
    public void ValidateDeletePassword_OtherCasingOrSubstrings_Rejected(string input)
    {
        Assert.False(SafetyGuard.ValidateDeletePassword(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ValidateDeletePassword_NullEmptyOrBlank_Rejected(string? input)
    {
        Assert.False(SafetyGuard.ValidateDeletePassword(input));
    }
}
