using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>依赖页的工具探测与镜像测速容错。</summary>
public class ToolsViewModelTests
{
    [Fact]
    public async Task RescanToolsAsync_ReflectsRealToolLocatorPaths()
    {
        using var host = new DesktopTestHost();
        var vm = host.Get<ToolsViewModel>();

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

    [Theory]
    [InlineData(":::invalid-url:::")]
    [InlineData("http://   ")]
    [InlineData("htp://bad-scheme")]
    [InlineData("ftp://invalid.host.xyz")]
    [InlineData("https://this-is-a-completely-non-existent-domain-9876543210.xyz")]
    public async Task TestMirrorSpeedAsync_InvalidUrls_HandlesGracefullyWithoutCrashing(string invalidUrl)
    {
        using var host = new DesktopTestHost();
        var vm = host.Get<ToolsViewModel>();
        vm.CustomMirrorUrl = invalidUrl;

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
    public async Task TestMirrorSpeedAsync_EmptyOrWhitespaceUrl_DefaultsSafelyWithoutCrashing(string? emptyUrl)
    {
        using var host = new DesktopTestHost();
        var vm = host.Get<ToolsViewModel>();
        vm.CustomMirrorUrl = emptyUrl!;

        var exception = await Record.ExceptionAsync(() => vm.TestMirrorSpeedAsync());
        Assert.Null(exception);
        Assert.False(string.IsNullOrWhiteSpace(vm.PingLatencyText));
    }

    [Fact]
    public async Task TestMirrorSpeedAsync_NonRoutableTimeout_HandlesGracefullyWithoutCrashing()
    {
        using var host = new DesktopTestHost();
        var vm = host.Get<ToolsViewModel>();
        // 10.255.255.1 是私有不可路由黑洞 IP，通常触发 5 秒 HttpClient 超时
        vm.CustomMirrorUrl = "http://10.255.255.1:65432";

        var exception = await Record.ExceptionAsync(() => vm.TestMirrorSpeedAsync());

        Assert.Null(exception);
        Assert.False(vm.IsPingHealthy);
        Assert.Contains("连接失败", vm.PingLatencyText);
    }
}
