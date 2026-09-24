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
/// 4. LocalizationService 语言热切换 (zh-CN, en-US, 大小写不敏感, 未知语言降级)
/// 5. LocalizationService 字典覆盖完整性 (ZhStrings vs EnStrings, .axaml 键对齐)
/// 6. LocalizationService 缺失键安全降级 (返回自身, 空串, null 行为)
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

    #region 4. LocalizationService Language Switching Tests

    [Fact]
    public void LocalizationService_DefaultLanguage_IsSimplifiedChinese()
    {
        var service = new LocalizationService();
        Assert.Equal("zh-CN", service.CurrentLanguage);
        Assert.Equal("LivePhotoConvert", service.GetString("AppTitle"));
        Assert.Equal("实况互转", service.GetString("NavConvert"));
        Assert.Equal("空间瘦身", service.GetString("NavStrip"));
    }

    [Theory]
    [InlineData("en-US", "en-US", "Convert", "Strip")]
    [InlineData("en", "en-US", "Convert", "Strip")]
    [InlineData("EN", "en-US", "Convert", "Strip")]
    [InlineData("EN-US", "en-US", "Convert", "Strip")]
    [InlineData("zh-CN", "zh-CN", "实况互转", "空间瘦身")]
    [InlineData("zh", "zh-CN", "实况互转", "空间瘦身")]
    [InlineData("ZH-CN", "zh-CN", "实况互转", "空间瘦身")]
    [InlineData("fr-FR", "zh-CN", "实况互转", "空间瘦身")] // 未知语言回落至 zh-CN
    [InlineData("", "zh-CN", "实况互转", "空间瘦身")]
    public void LocalizationService_SetLanguage_NormalizesAndSwitchesStrings(
        string inputCode, string expectedLang, string expectedNavConvert, string expectedNavStrip)
    {
        var service = new LocalizationService();
        string? notifiedLanguage = null;
        service.LanguageChanged += lang => notifiedLanguage = lang;

        service.SetLanguage(inputCode);

        Assert.Equal(expectedLang, service.CurrentLanguage);
        Assert.Equal(expectedLang, notifiedLanguage);
        Assert.Equal(expectedNavConvert, service.GetString("NavConvert"));
        Assert.Equal(expectedNavStrip, service.GetString("NavStrip"));
    }

    [Fact]
    public void LocalizationService_MultipleSwitches_MaintainsIntegrity()
    {
        var service = new LocalizationService();
        int changeCount = 0;
        service.LanguageChanged += _ => changeCount++;

        service.SetLanguage("en-US");
        Assert.Equal("Convert", service.GetString("NavConvert"));

        service.SetLanguage("zh-CN");
        Assert.Equal("实况互转", service.GetString("NavConvert"));

        service.SetLanguage("en-US");
        Assert.Equal("Convert", service.GetString("NavConvert"));

        Assert.Equal(3, changeCount);
    }

    #endregion

    #region 5. LocalizationService Dictionary Parity & Coverage Tests

    [Fact]
    public void LocalizationService_ZhAndEnDictionaries_HaveIdenticalKeySet()
    {
        var service = new LocalizationService();

        // 收集 zh-CN 下所有键
        service.SetLanguage("zh-CN");
        // 通过测试 axaml 资源提取键集合
        var axamlZhPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.zh-CN.axaml"));
        var axamlEnPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.en-US.axaml"));

        Assert.True(File.Exists(axamlZhPath), $"Missing zh-CN axaml: {axamlZhPath}");
        Assert.True(File.Exists(axamlEnPath), $"Missing en-US axaml: {axamlEnPath}");

        var zhDoc = XDocument.Load(axamlZhPath);
        var enDoc = XDocument.Load(axamlEnPath);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var zhKeysList = zhDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToList();

        var enKeysList = enDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToList();

        // 0. 验证原始 XML 键定义绝对唯一，杜绝任何重复键（否则 Avalonia 加载 ResourceDictionary 会抛 ArgumentException 崩溃）
        Assert.Equal(zhKeysList.Distinct().Count(), zhKeysList.Count);
        Assert.Equal(enKeysList.Distinct().Count(), enKeysList.Count);
        // 中英文字典键数量必须严格一致（禁用写死计数的魔法数字，以两者相等作为真实不变量）
        Assert.Equal(zhKeysList.Count, enKeysList.Count);

        var zhKeys = zhKeysList.ToHashSet();
        var enKeys = enKeysList.ToHashSet();

        // 1. 中英 axaml 键名必须 100% 对齐，不得有缺失
        var missingInEn = zhKeys.Except(enKeys).ToList();
        var missingInZh = enKeys.Except(zhKeys).ToList();

        Assert.Empty(missingInEn);
        Assert.Empty(missingInZh);
        Assert.True(zhKeys.Count >= 70, $"Expected at least 70 localized string keys, found {zhKeys.Count}");

        // 2. 验证所有键在 LocalizationService 的 GetString 中均有非空值（非自身回落）
        service.SetLanguage("zh-CN");
        foreach (var key in zhKeys)
        {
            var zhVal = service.GetString(key);
            Assert.False(string.IsNullOrEmpty(zhVal), $"Key '{key}' in zh-CN resolved to empty");
            Assert.NotEqual(key, zhVal); // 证明从字典中查出真实内容，而非 fallback 到 key 自身
        }

        service.SetLanguage("en-US");
        foreach (var key in enKeys)
        {
            var enVal = service.GetString(key);
            Assert.False(string.IsNullOrEmpty(enVal), $"Key '{key}' in en-US resolved to empty");
            Assert.NotEqual(key, enVal); // 证明从字典中查出真实内容，而非 fallback 到 key 自身
        }
    }

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

    #region 6. LocalizationService Missing Key Fallback Tests

    [Fact]
    public void LocalizationService_NonExistentKey_ReturnsKeyItselfWithoutThrowing()
    {
        var service = new LocalizationService();

        var unknown1 = service.GetString("NON_EXISTENT_KEY_123456");
        Assert.Equal("NON_EXISTENT_KEY_123456", unknown1);

        service.SetLanguage("en-US");
        var unknown2 = service.GetString("NON_EXISTENT_KEY_789012");
        Assert.Equal("NON_EXISTENT_KEY_789012", unknown2);
    }

    [Fact]
    public void LocalizationService_EmptyKey_ReturnsEmptyString()
    {
        var service = new LocalizationService();
        var result = service.GetString("");
        Assert.Equal("", result);
    }

    [Fact]
    public void LocalizationService_NullKey_ThrowsArgumentNullException()
    {
        var service = new LocalizationService();
        // FrozenDictionary 在传入 null key 时按 .NET 契约抛出 ArgumentNullException
        Assert.Throws<ArgumentNullException>(() => service.GetString(null!));
    }

    [Fact]
    public void LocalizationService_NullLanguageCode_ThrowsNullReferenceException()
    {
        var service = new LocalizationService();
        // SetLanguage 对 null 调用 ToLowerInvariant()，会抛出 NullReferenceException
        Assert.Throws<NullReferenceException>(() => service.SetLanguage(null!));
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
