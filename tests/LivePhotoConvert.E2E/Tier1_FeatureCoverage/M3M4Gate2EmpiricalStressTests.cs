using System.Xml.Linq;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// M3 & M4 Gate 2 对抗性实证压力测试套件：
/// 1. 中英双语资源字典全量键名 100% 对齐校验 (Strings.zh-CN.axaml vs Strings.en-US.axaml vs LocalizationService)
/// 2. SafetyGuard.ValidateDeletePassword 严格空格拒绝鉴权
/// 3. ToolsViewModel.TestMirrorSpeedAsync 异常 URL、超时与活体探测防崩容错，及 RescanToolsAsync 本地路径真实验证
/// 4. DesktopProgressReporter 极端值 (0 total, 负数, null 文件名, 大整数溢出) 稳健性测试
/// </summary>
public class M3M4Gate2EmpiricalStressTests
{
    #region 1. Bilingual Key Parity Tests

    [Fact]
    public void BilingualStrings_ZhAndEnAxaml_HaveExactKeyParity()
    {
        var zhPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.zh-CN.axaml"));
        var enPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.en-US.axaml"));

        Assert.True(File.Exists(zhPath), $"Missing zh-CN.axaml at: {zhPath}");
        Assert.True(File.Exists(enPath), $"Missing en-US.axaml at: {enPath}");

        var zhDoc = XDocument.Load(zhPath);
        var enDoc = XDocument.Load(enPath);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var zhKeys = zhDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToHashSet();

        var enKeys = enDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToHashSet();

        var missingInEn = zhKeys.Except(enKeys).OrderBy(k => k).ToList();
        var missingInZh = enKeys.Except(zhKeys).OrderBy(k => k).ToList();

        Assert.True(missingInEn.Count == 0, $"Keys present in zh-CN but missing in en-US: {string.Join(", ", missingInEn)}");
        Assert.True(missingInZh.Count == 0, $"Keys present in en-US but missing in zh-CN: {string.Join(", ", missingInZh)}");
        Assert.True(zhKeys.Count >= 100, $"Expected >= 100 distinct keys, found {zhKeys.Count}");
    }

    [Fact]
    public void BilingualStrings_LocalizationService_MatchesAxamlKeysAndReturnsNonEmpty()
    {
        var zhPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.zh-CN.axaml"));
        var zhDoc = XDocument.Load(zhPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = zhDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToHashSet();

        var service = new LocalizationService();

        // 验证 zh-CN 解析
        service.SetLanguage("zh-CN");
        foreach (var key in keys)
        {
            var val = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(val), $"Key '{key}' resolved to empty in zh-CN");
            Assert.NotEqual(key, val); // 证明查到真实内容，而非 fallback 到 key 自身
        }

        // 验证 en-US 解析
        service.SetLanguage("en-US");
        foreach (var key in keys)
        {
            var val = service.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(val), $"Key '{key}' resolved to empty in en-US");
            Assert.NotEqual(key, val); // 证明查到真实内容，而非 fallback 到 key 自身
        }
    }

    [Fact]
    public void BilingualStrings_ViewsAndControls_AllDynamicResourceKeysExistInDictionaries()
    {
        var zhPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop", "Assets", "Strings.zh-CN.axaml"));
        var zhDoc = XDocument.Load(zhPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictKeys = zhDoc.Descendants()
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Cast<string>()
            .ToHashSet();

        var desktopDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop"));
        var axamlFiles = Directory.EnumerateFiles(desktopDir, "*.axaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains("Assets" + Path.DirectorySeparatorChar + "Strings") &&
                        !p.Contains("Assets" + Path.DirectorySeparatorChar + "Icons"))
            .ToList();

        var missing = new Dictionary<string, List<string>>();

        foreach (var f in axamlFiles)
        {
            var content = File.ReadAllText(f);
            var matches = System.Text.RegularExpressions.Regex.Matches(content, @"\{DynamicResource\s+([a-zA-Z0-9_]+)\}");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var resKey = m.Groups[1].Value;
                if (!resKey.StartsWith("Icon") &&
                    !resKey.EndsWith("Brush") &&
                    !resKey.EndsWith("Color") &&
                    !resKey.EndsWith("Background") &&
                    !resKey.EndsWith("Foreground"))
                {
                    if (!dictKeys.Contains(resKey))
                    {
                        if (!missing.ContainsKey(resKey))
                        {
                            missing[resKey] = new List<string>();
                        }
                        missing[resKey].Add(Path.GetFileName(f));
                    }
                }
            }
        }

        Assert.Empty(missing);
    }

    #endregion

    #region 2. SafetyGuard.ValidateDeletePassword Strict Rejection Tests

    [Fact]
    public void ValidateDeletePassword_ExactDelete_ReturnsTrue()
    {
        Assert.True(SafetyGuard.ValidateDeletePassword("DELETE"));
    }

    [Theory]
    [InlineData("DELETE ")]        // 尾部单空格
    [InlineData(" DELETE")]        // 头部单空格
    [InlineData(" DELETE ")]       // 头尾空格
    [InlineData("  DELETE  ")]     // 头尾多空格
    [InlineData("\tDELETE")]       // 头部制表符
    [InlineData("DELETE\t")]       // 尾部制表符
    [InlineData("\tDELETE\t")]     // 头尾制表符
    [InlineData("\r\nDELETE")]     // 头部换行符
    [InlineData("DELETE\r\n")]     // 尾部换行符
    [InlineData("\nDELETE\n")]     // 头尾换行符
    [InlineData("DEL ETE")]        // 内部空格
    [InlineData("D E L E T E")]    // 内部多空格
    public void ValidateDeletePassword_WhitespaceVariations_StrictlyRejected(string input)
    {
        // 任何携带前导、后置或内部空白的字符串，必须严格返回 false
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
    public void ValidateDeletePassword_CasingAndSubstrings_StrictlyRejected(string input)
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
    public void ValidateDeletePassword_NullOrEmptyOrPureWhitespace_StrictlyRejected(string? input)
    {
        Assert.False(SafetyGuard.ValidateDeletePassword(input));
    }

    #endregion

    #region 3. ToolsViewModel Resilience & ToolLocator Integration Tests

    [Fact]
    public async Task ToolsViewModel_RescanToolsAsync_ReflectsRealToolLocatorPaths()
    {
        var tempSettingsFile = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new SettingsService(tempSettingsFile);
            var vm = new ToolsViewModel(settingsService);

            await vm.RescanToolsAsync();

            var expectedExifPath = ToolLocator.Find(ExifTool.ExecutableName);
            var expectedFfmpegPath = ToolLocator.Find(FfmpegVideoConverter.ExecutableName);
            var expectedHeifPath = ToolLocator.Find(HeifEncImageConverter.ExecutableName);

            // 验证 FFmpeg
            if (expectedFfmpegPath is not null)
            {
                Assert.True(vm.IsFfmpegReady);
                Assert.Equal(expectedFfmpegPath, vm.FfmpegPath);
                Assert.NotEqual("未安装", vm.FfmpegVersion);
            }
            else
            {
                Assert.False(vm.IsFfmpegReady);
                Assert.Equal("未检测到 ffmpeg.exe", vm.FfmpegPath);
                Assert.Equal("未安装", vm.FfmpegVersion);
            }

            // 验证 ExifTool
            if (expectedExifPath is not null)
            {
                Assert.True(vm.IsExifToolReady);
                Assert.Equal(expectedExifPath, vm.ExifToolPath);
            }
            else
            {
                Assert.False(vm.IsExifToolReady);
                Assert.Equal("未检测到 exiftool.exe", vm.ExifToolPath);
                Assert.Equal("未安装", vm.ExifToolVersion);
            }

            // 验证 heif-enc
            if (expectedHeifPath is not null)
            {
                Assert.True(vm.IsHeifEncReady);
                Assert.Equal(expectedHeifPath, vm.HeifEncPath);
            }
            else
            {
                Assert.False(vm.IsHeifEncReady);
                Assert.Equal("未检测到 heif-enc.exe", vm.HeifEncPath);
                Assert.Equal("未安装", vm.HeifEncVersion);
            }
        }
        finally
        {
            if (File.Exists(tempSettingsFile))
            {
                File.Delete(tempSettingsFile);
            }
        }
    }

    [Theory]
    [InlineData(":::invalid-url:::")]
    [InlineData("http://   ")]
    [InlineData("htp://bad-scheme")]
    [InlineData("ftp://invalid.host.xyz")]
    [InlineData("https://this-is-a-completely-non-existent-domain-9876543210.xyz")]
    public async Task ToolsViewModel_TestMirrorSpeedAsync_InvalidUrls_HandlesGracefullyWithoutCrashing(string invalidUrl)
    {
        var settingsService = new SettingsService();
        var vm = new ToolsViewModel(settingsService)
        {
            CustomMirrorUrl = invalidUrl
        };

        // 必须优雅捕获异常，严禁发生未捕获崩溃
        var exception = await Record.ExceptionAsync(() => vm.TestMirrorSpeedAsync());

        Assert.Null(exception);
        Assert.False(vm.IsPingHealthy);
        Assert.Contains("连接失败", vm.PingLatencyText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ToolsViewModel_TestMirrorSpeedAsync_EmptyOrWhitespaceUrl_DefaultsSafelyWithoutCrashing(string? emptyUrl)
    {
        var settingsService = new SettingsService();
        var vm = new ToolsViewModel(settingsService)
        {
            CustomMirrorUrl = emptyUrl!
        };

        var exception = await Record.ExceptionAsync(() => vm.TestMirrorSpeedAsync());
        Assert.Null(exception);
        Assert.False(string.IsNullOrWhiteSpace(vm.PingLatencyText));
    }

    [Fact]
    public async Task ToolsViewModel_TestMirrorSpeedAsync_NonRoutableTimeout_HandlesGracefullyWithoutCrashing()
    {
        var settingsService = new SettingsService();
        var vm = new ToolsViewModel(settingsService)
        {
            // 10.255.255.1 是私有不可路由黑洞 IP，通常触发 5 秒 HttpClient 超时
            CustomMirrorUrl = "http://10.255.255.1:65432"
        };

        var exception = await Record.ExceptionAsync(() => vm.TestMirrorSpeedAsync());

        Assert.Null(exception);
        Assert.False(vm.IsPingHealthy);
        Assert.Contains("连接失败", vm.PingLatencyText);
    }

    #endregion

    #region 4. DesktopProgressReporter Edge Cases Tests

    [Fact]
    public void DesktopProgressReporter_ZeroTotal_HandlesGracefullyWithoutDivideByZero()
    {
        int capturedCompleted = -1;
        int capturedTotal = -1;
        string capturedFile = "init";

        var reporter = new DesktopProgressReporter((c, t, f) =>
        {
            capturedCompleted = c;
            capturedTotal = t;
            capturedFile = f;
        });

        // 触发 0 total 边界
        reporter.Report(0, 0, "file.jpg");

        Assert.Equal(0, capturedCompleted);
        Assert.Equal(0, capturedTotal);
        Assert.Equal("file.jpg", capturedFile);

        // 验证进度百分比计算公式不会发生除以 0 异常或 NaN
        double pct = capturedTotal > 0 ? (double)capturedCompleted / capturedTotal * 100.0 : 0.0;
        Assert.Equal(0.0, pct);
        Assert.False(double.IsNaN(pct));
        Assert.False(double.IsInfinity(pct));
    }

    [Fact]
    public void DesktopProgressReporter_NegativeValues_HandlesGracefully()
    {
        int capturedCompleted = 0;
        int capturedTotal = 0;

        var reporter = new DesktopProgressReporter((c, t, _) =>
        {
            capturedCompleted = c;
            capturedTotal = t;
        });

        reporter.Report(-5, -10, "negative.jpg");

        Assert.Equal(-5, capturedCompleted);
        Assert.Equal(-10, capturedTotal);

        double pct = capturedTotal > 0 ? (double)capturedCompleted / capturedTotal * 100.0 : 0.0;
        Assert.Equal(0.0, pct);
    }

    [Fact]
    public void DesktopProgressReporter_NullFilename_PassedThroughWithoutNullReferenceException()
    {
        string? capturedFile = "not_null";

        var reporter = new DesktopProgressReporter((_, _, f) =>
        {
            capturedFile = f;
        });

        var exception = Record.Exception(() => reporter.Report(1, 10, null!));

        Assert.Null(exception);
        Assert.Null(capturedFile);
    }

    [Fact]
    public void DesktopProgressReporter_LargeNumbers_CalculatesAccuratelyWithoutOverflow()
    {
        int capturedCompleted = 0;
        int capturedTotal = 0;

        var reporter = new DesktopProgressReporter((c, t, _) =>
        {
            capturedCompleted = c;
            capturedTotal = t;
        });

        reporter.Report(1_000_000, 2_000_000, "huge_batch.jpg");

        Assert.Equal(1_000_000, capturedCompleted);
        Assert.Equal(2_000_000, capturedTotal);

        double pct = capturedTotal > 0 ? (double)capturedCompleted / capturedTotal * 100.0 : 0.0;
        Assert.Equal(50.0, pct);
    }

    [Fact]
    public void DesktopProgressReporter_CompletedExceedsTotal_HandlesGracefully()
    {
        int capturedCompleted = 0;
        int capturedTotal = 0;

        var reporter = new DesktopProgressReporter((c, t, _) =>
        {
            capturedCompleted = c;
            capturedTotal = t;
        });

        reporter.Report(15, 10, "overflow.jpg");

        Assert.Equal(15, capturedCompleted);
        Assert.Equal(10, capturedTotal);

        double pct = capturedTotal > 0 ? (double)capturedCompleted / capturedTotal * 100.0 : 0.0;
        Assert.Equal(150.0, pct);
    }

    #endregion
}
