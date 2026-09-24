using System.Xml.Linq;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// 对抗性挑战套件：Milestone 3 & Milestone 4 核心安全防灾矩阵与动态本地化服务
/// 覆盖：
/// 1. SafetyGuard.CheckDiskSpace ENOSPC 边界条件 (0B, 1B, 100GB, 非法路径)
/// 2. SafetyGuard.ValidateDeletePassword DELETE 密码鉴权 (大小写敏感、空格处理、空串/null)
/// 3. SafetyGuard 原子暂存与回滚 (.livephoto_backup 提交、回滚、目录不存在异常恢复)
/// 4. Strings XAML 可无冲突地装入 ResourceDictionary（Localizer 行为见 LocalizationResourceTests）
/// </summary>
public class SafetyGuardAndLocalizationTests
{
    #region 1. SafetyGuard ENOSPC Boundary Tests

    [Fact]
    public void CheckDiskSpace_ZeroBytes_CalculatesExact500MBReserve()
    {
        const long totalSourceBytes = 0L;
        const long expected500MB = 500L * 1024 * 1024; // 524,288,000 bytes

        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", totalSourceBytes);

        // 0 * 1.2 + 500MB = 500MB
        Assert.Equal(expected500MB, requiredBytes);
        Assert.True(availableBytes >= 0);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_OneByte_CalculatesExactReservePlusOneByte()
    {
        const long totalSourceBytes = 1L;
        // (long)(1 * 1.2) = 1 byte
        const long expectedBytes = 1L + (500L * 1024 * 1024);

        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", totalSourceBytes);

        Assert.Equal(expectedBytes, requiredBytes);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_100GB_CalculatesExactWithoutPrecisionLoss()
    {
        const long oneGB = 1024L * 1024 * 1024;
        const long totalSourceBytes = 100L * oneGB; // 107,374,182,400 bytes
        // 100GB * 1.2 = 120GB = 128,849,018,880 bytes
        // + 500MB = 129,373,306,880 bytes
        const long expectedRequired = (long)(100L * oneGB * 1.2) + (500L * 1024 * 1024);

        var (hasSpace, requiredBytes, availableBytes) = SafetyGuard.CheckDiskSpace(".", totalSourceBytes);

        Assert.Equal(expectedRequired, requiredBytes);
        Assert.Equal(129_373_306_880L, requiredBytes);
        Assert.Equal(availableBytes >= requiredBytes, hasSpace);
    }

    [Fact]
    public void CheckDiskSpace_InvalidOrNonExistentDirectory_FallsBackSafelyWithoutCrashing()
    {
        // 传入非法/不存在路径 (如非常规网络驱动器或无效格式)
        const long totalSourceBytes = 10L * 1024 * 1024;
        const long expectedRequired = (long)(10L * 1024 * 1024 * 1.2) + (500L * 1024 * 1024);

        // 极端路径: 非法字符或无根路径
        var result = SafetyGuard.CheckDiskSpace("::InvalidDrive:\\path", totalSourceBytes);

        // 安全矩阵在无法识别驱动器时应具备防崩兜底策略
        Assert.Equal(expectedRequired, result.RequiredBytes);
        Assert.True(result.HasEnoughSpace);
    }

    #endregion

    #region 2. SafetyGuard DELETE Password Authorization Tests

    [Theory]
    [InlineData("DELETE", true)]
    [InlineData("delete", false)]
    [InlineData("Delete", false)]
    [InlineData("DeLeTe", false)]
    [InlineData("dELETE", false)]
    [InlineData("DEL", false)]
    [InlineData("DELETE1", false)]
    [InlineData("DELETE_NOW", false)]
    [InlineData("DELET", false)]
    public void ValidateDeletePassword_CaseSensitivityAndAccuracy(string input, bool expectedValid)
    {
        var result = SafetyGuard.ValidateDeletePassword(input);
        Assert.Equal(expectedValid, result);
    }

    [Theory]
    [InlineData(" DELETE", false)]
    [InlineData("DELETE ", false)]
    [InlineData("   DELETE   ", false)]
    [InlineData("\tDELETE\t", false)]
    [InlineData("\r\nDELETE\r\n", false)]
    [InlineData("DEL ETE", false)]   // 内部包含空格
    [InlineData("D E L E T E", false)]
    public void ValidateDeletePassword_WhitespaceHandling_StrictWhitespaceRejection(string input, bool expectedValid)
    {
        // SafetyGuard 契约规定采用严格序号匹配，拒绝任何首尾空白误触
        var result = SafetyGuard.ValidateDeletePassword(input);
        Assert.Equal(expectedValid, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("   ", false)]
    [InlineData("\t", false)]
    [InlineData("\n", false)]
    public void ValidateDeletePassword_NullOrEmptyOrPureWhitespace_ReturnsFalse(string? input, bool expectedValid)
    {
        var result = SafetyGuard.ValidateDeletePassword(input);
        Assert.Equal(expectedValid, result);
    }

    #endregion

    #region 4. Strings XAML Loading Tests

    [Fact]
    public void Avalonia_ResourceDictionary_CanLoadAllKeysWithoutCollision()
    {
        var axamlZhPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.zh-CN.axaml"));
        var axamlEnPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.en-US.axaml"));

        var zhDoc = XDocument.Load(axamlZhPath);
        var enDoc = XDocument.Load(axamlEnPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var zhDict = new Avalonia.Controls.ResourceDictionary();
        foreach (var el in zhDoc.Descendants().Where(e => e.Attribute(x + "Key") != null))
        {
            var key = el.Attribute(x + "Key")!.Value;
            var val = el.Value;
            zhDict.Add(key, val); // Throws ArgumentException if duplicate key exists
        }

        var enDict = new Avalonia.Controls.ResourceDictionary();
        foreach (var el in enDoc.Descendants().Where(e => e.Attribute(x + "Key") != null))
        {
            var key = el.Attribute(x + "Key")!.Value;
            var val = el.Value;
            enDict.Add(key, val); // Throws ArgumentException if duplicate key exists
        }

        // 中英文字典加载后键数量必须严格一致（禁用写死计数的魔法数字）
        Assert.Equal(zhDict.Count, enDict.Count);
    }

    #endregion

    #region 7. MinWidthToBoolConverter Tests

    [Theory]
    [InlineData(600.0, "500", true)]
    [InlineData(500.0, "500", true)]
    [InlineData(499.9, "500", false)]
    [InlineData(0.0, "500", false)]
    [InlineData(450.0, "<520", true)]
    [InlineData(519.9, "<520", true)]
    [InlineData(520.0, "<520", false)]
    [InlineData(600.0, "<520", false)]
    [InlineData(450.0, "!520", true)]
    [InlineData(520.0, "!520", false)]
    public void MinWidthToBoolConverter_ConvertsCorrectly(double width, string param, bool expected)
    {
        var converter = Desktop.Converters.MinWidthToBoolConverter.Instance;
        var result = converter.Convert(width, typeof(bool), param, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MinWidthToBoolConverter_InvalidInputs_ReturnsFalse()
    {
        var converter = Desktop.Converters.MinWidthToBoolConverter.Instance;
        Assert.False((bool)converter.Convert("invalid", typeof(bool), "500", System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.False((bool)converter.Convert(null, typeof(bool), "500", System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.Throws<NotSupportedException>(() => converter.ConvertBack(true, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    #endregion
}
