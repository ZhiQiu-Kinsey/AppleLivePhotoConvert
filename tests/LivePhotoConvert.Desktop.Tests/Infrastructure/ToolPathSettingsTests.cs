using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Tools;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

/// <summary>工具路径设置与探测缓存失效，以及组合根中的工具服务装配。</summary>
public class ToolPathSettingsTests
{
    [Fact]
    public void SetAndGet_MapEachToolToItsOwnFieldAndTreatBlankAsAuto()
    {
        using var host = new DesktopTestHost();
        var paths = new ToolPathSettings(host.Settings);

        paths.Set(ToolId.ExifTool, "/a/exiftool");
        paths.Set(ToolId.Ffmpeg, "/b/ffmpeg");
        paths.Set(ToolId.HeifEnc, "   ");

        Assert.Equal("/a/exiftool", host.Settings.Current.ExifToolPath);
        Assert.Equal("/b/ffmpeg", host.Settings.Current.FfmpegPath);
        Assert.Equal(string.Empty, host.Settings.Current.HeifEncPath);
        Assert.Equal("/a/exiftool", paths.Get(ToolId.ExifTool));
        Assert.Null(paths.Get(ToolId.HeifEnc));

        paths.Set(ToolId.Ffmpeg, null);
        Assert.Equal(string.Empty, host.Settings.Current.FfmpegPath);
    }

    [Fact]
    public void Set_InvalidatesOnlyChangedTool()
    {
        using var host = new DesktopTestHost();
        var paths = new ToolPathSettings(host.Settings);
        var registry = new FakeToolRegistry();
        paths.InvalidateOnChange(registry);

        paths.Set(ToolId.Ffmpeg, "/b/ffmpeg");
        paths.Set(ToolId.Ffmpeg, "/b/ffmpeg");
        paths.Set(ToolId.ExifTool, null);

        Assert.Equal([ToolId.Ffmpeg], registry.Invalidations);
    }

    [Fact]
    public async Task CompositionRoot_WiresRegistryToSettingsAndInstallerToToolDirectory()
    {
        using var host = new DesktopTestHost();

        var registry = Assert.IsType<ToolRegistry>(host.Get<IToolRegistry>());
        var installer = Assert.IsType<ToolInstaller>(host.Get<IToolInstaller>());
        Assert.Same(ToolManifest.Embedded, installer.Manifest);
        Assert.Contains(installer.InstallRoot, ToolDirectories.CandidateToolRoots());
        Assert.IsType<TaskCenterToolUsage>(host.Get<IToolUsage>());

        var invalidated = new List<ToolId?>();
        registry.Invalidated += (_, tool) => invalidated.Add(tool);
        var missing = Path.Combine(host.Directory, "nowhere", "ffmpeg");
        host.Get<ToolPathSettings>().Set(ToolId.Ffmpeg, missing);

        Assert.Equal([ToolId.Ffmpeg], invalidated);
        var info = await registry.GetAsync(ToolId.Ffmpeg, TestContext.Current.CancellationToken);
        Assert.True(info.IsExplicitPathInvalid, "注册表必须读到设置中的新路径");
    }
}
