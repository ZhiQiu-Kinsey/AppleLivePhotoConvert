using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// M3 & M4 Gate 2 对抗性实证压力测试套件：
/// 2. SafetyGuard.ValidateDeletePassword 严格空格拒绝鉴权
/// 3. ToolsViewModel.TestMirrorSpeedAsync 异常 URL、超时与活体探测防崩容错，及 RescanToolsAsync 本地路径真实验证
/// 4. DesktopProgressReporter 极端值 (0 total, 负数, null 文件名, 大整数溢出) 稳健性测试
/// </summary>
public class LocalizationParityAndToolsTests
{
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
            var vm = new ToolsViewModel(settingsService, Localizer.Current);

            await vm.RescanToolsAsync();

            var expectedExifPath = ToolLocator.Find(ExifToolMetadataService.ExecutableName);
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
        var vm = new ToolsViewModel(settingsService, Localizer.Current)
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
        var vm = new ToolsViewModel(settingsService, Localizer.Current)
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
        var vm = new ToolsViewModel(settingsService, Localizer.Current)
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

}
