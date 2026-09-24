using System.Net;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>依赖页：状态与能力展示、升级与无效路径提示、镜像地址与连通性、手动指定路径。</summary>
[Collection(ProcessStateCollection.Name)]
public class ToolsViewModelTests
{
    private const ToolCapabilities FullFfmpeg = ToolCapabilities.Hdr | ToolCapabilities.Libx264;

    [Fact]
    public void Cards_ShowStatusVersionNumberPathAndCapabilities()
    {
        using var f = new ToolsFixture(arrange: r =>
        {
            r.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/opt/ffmpeg/ffmpeg", "n7.0-7-gd38bf5e08e-20240407", FullFfmpeg, recommended: "7.0");
            r.Infos[ToolId.HeifEnc] = FakeToolRegistry.Found(ToolId.HeifEnc, "/usr/bin/heif-enc", "1.23.4", recommended: "1.23.4");
        });
        var vm = f.ViewModel;

        Assert.Equal([ToolId.ExifTool, ToolId.Ffmpeg, ToolId.HeifEnc], vm.Cards.Select(c => c.Tool));

        var ffmpeg = vm.Ffmpeg;
        Assert.True(ffmpeg.IsReady);
        Assert.True(ffmpeg.IsHealthy);
        Assert.False(ffmpeg.HasWarning);
        Assert.Equal("7.0", ffmpeg.StatusText);
        Assert.Equal("n7.0-7-gd38bf5e08e-20240407", ffmpeg.VersionDetail);
        Assert.Equal("/opt/ffmpeg/ffmpeg", ffmpeg.PathText);
        Assert.Equal(
            [new CapabilityBadge(f.Localizer["ToolCapabilityHdrTonemap"], true), new CapabilityBadge(f.Localizer["ToolCapabilityHevc10Bit"], true)],
            ffmpeg.Capabilities);
        Assert.False(ffmpeg.IsHdrUnavailable);
        Assert.False(ffmpeg.ShowInstallButton);

        Assert.Empty(vm.HeifEnc.Capabilities);
        Assert.False(vm.HeifEnc.HasCapabilities);

        var exif = vm.ExifTool;
        Assert.True(exif.IsMissing);
        Assert.Equal(f.Localizer["ToolNotInstalled"], exif.StatusText);
        Assert.Equal(f.Localizer.Format("ToolNotDetectedFormat", exif.ExecutableName), exif.PathText);
        Assert.True(exif.ShowInstallButton);
        Assert.Equal(f.Localizer["InstallToolBtn"], exif.InstallButtonText);

        Assert.False(vm.IsExifToolReady);
        Assert.True(vm.IsFfmpegReady);
        Assert.True(vm.IsHeifEncReady);
        Assert.False(vm.AllToolsReady);
    }

    [Fact]
    public void VersionBadge_DistroBuild_ShowsOnlyNumericVersion()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/usr/bin/ffmpeg", "6.1.1-3ubuntu5", FullFfmpeg));

        Assert.Equal("6.1.1", f.ViewModel.Ffmpeg.StatusText);
        Assert.Equal("6.1.1-3ubuntu5", f.ViewModel.Ffmpeg.VersionDetail);
    }

    [Fact]
    public void Ffmpeg_WithoutHdrChain_ShowsUnsupportedBadgesAndHdrHint()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/usr/bin/ffmpeg", "6.1.1", ToolCapabilities.Libx264 | ToolCapabilities.Libx265 | ToolCapabilities.Tonemap));
        var ffmpeg = f.ViewModel.Ffmpeg;

        Assert.All(ffmpeg.Capabilities, badge => Assert.False(badge.IsSupported));
        Assert.True(ffmpeg.IsHdrUnavailable);
        Assert.True(ffmpeg.HasWarning);
        Assert.False(ffmpeg.IsHealthy);
        Assert.True(ffmpeg.IsReady);
    }

    [Fact]
    public void Ffmpeg_ProbeError_DoesNotClaimMissingCapabilities()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.Ffmpeg] = new ToolInfo(ToolId.Ffmpeg, "/usr/bin/ffmpeg", null, null, ToolCapabilities.None, null, false, "timeout"));
        var ffmpeg = f.ViewModel.Ffmpeg;

        Assert.Equal(f.Localizer["ToolVersionUnknown"], ffmpeg.StatusText);
        Assert.Empty(ffmpeg.Capabilities);
        Assert.False(ffmpeg.IsHdrUnavailable);
        Assert.True(ffmpeg.HasWarning);
    }

    [Fact]
    public void OlderThanRecommended_ShowsUpgradeHintAndButton()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, "/usr/bin/exiftool", "12.76", recommended: "13.59"));
        var exif = f.ViewModel.ExifTool;

        Assert.True(exif.IsUpdateRecommended);
        Assert.True(exif.HasWarning);
        Assert.Equal(f.Localizer.Format("ToolUpdateAvailableFormat", "13.59"), exif.UpdateText);
        Assert.True(exif.ShowInstallButton);
        Assert.Equal(f.Localizer.Format("ToolUpgradeBtnFormat", "13.59"), exif.InstallButtonText);
        Assert.Contains("13.59", exif.InstallButtonText);
    }

    [Fact]
    public void InvalidExplicitPath_IsFlaggedEvenWhenFallbackFound()
    {
        using var f = new ToolsFixture(arrange: r =>
        {
            r.Infos[ToolId.HeifEnc] = FakeToolRegistry.Found(ToolId.HeifEnc, "/usr/bin/heif-enc", "1.23.4", explicitInvalid: true);
            r.Infos[ToolId.ExifTool] = FakeToolRegistry.Missing(ToolId.ExifTool, explicitInvalid: true);
        });

        Assert.True(f.ViewModel.HeifEnc.IsExplicitPathInvalid);
        Assert.True(f.ViewModel.HeifEnc.HasWarning);
        Assert.True(f.ViewModel.ExifTool.IsExplicitPathInvalid);
        Assert.False(f.ViewModel.Ffmpeg.IsExplicitPathInvalid);
    }

    [Fact]
    public async Task Rescan_InvalidatesAllAndAppliesNewResults()
    {
        using var f = new ToolsFixture();
        Assert.False(f.ViewModel.IsExifToolReady);
        var changed = new List<string?>();
        f.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        f.Registry.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, "/usr/bin/exiftool", "13.59");
        await f.ViewModel.RescanToolsAsync();

        Assert.Contains(null, f.Registry.Invalidations);
        Assert.True(f.ViewModel.IsExifToolReady);
        Assert.Contains(nameof(ToolsViewModel.IsExifToolReady), changed);
        Assert.Contains(nameof(ToolsViewModel.AllToolsReady), changed);
    }

    [Fact]
    public void LanguageSwitch_RebuildsCardTextsAndMirrorNames()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/usr/bin/ffmpeg", "7.0", FullFfmpeg));
        f.ViewModel.SelectedMirrorPreset = f.ViewModel.MirrorPresets[0];
        var mirror = f.ViewModel.CustomMirrorUrl;

        f.Localizer.SetLanguage("en");

        Assert.Equal(f.Localizer["ToolNotInstalled"], f.ViewModel.ExifTool.StatusText);
        Assert.Equal(f.Localizer["ExifToolDesc"], f.ViewModel.ExifTool.Description);
        Assert.Equal(f.Localizer["ToolCapabilityHdrTonemap"], f.ViewModel.Ffmpeg.Capabilities[0].Label);
        Assert.All(f.ViewModel.Cards, c => Assert.False(UiTexts.ContainsChinese(c.StatusText + c.Description + c.InstallButtonText)));
        Assert.Equal(f.Localizer["MirrorPresetGhfast"], f.ViewModel.SelectedMirrorPreset?.Name);
        Assert.Equal(mirror, f.ViewModel.CustomMirrorUrl);
    }

    [Fact]
    public void MirrorInput_IsSavedOnceAfterTypingStops()
    {
        using var f = new ToolsFixture();
        var original = f.Settings.Current.CustomMirrorUrl;

        foreach (var partial in new[] { "h", "ht", "https://", "https://my.proxy/" })
        {
            f.ViewModel.CustomMirrorUrl = partial;
            f.Time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(original, f.Settings.Current.CustomMirrorUrl);
        f.Time.Advance(ToolsViewModel.MirrorSaveDelay);
        Assert.Equal("https://my.proxy/", f.Settings.Current.CustomMirrorUrl);

        f.Settings.Flush();
        using var reloaded = new SettingsStore(f.Settings.FilePath);
        Assert.Equal("https://my.proxy/", reloaded.Current.CustomMirrorUrl);
    }

    [Fact]
    public void MirrorPreset_FillsAddressAndMatchesSavedValueOnStartup()
    {
        using (var f = new ToolsFixture())
        {
            f.ViewModel.SelectedMirrorPreset = f.ViewModel.MirrorPresets[1];
            Assert.Equal("https://ghproxy.net/", f.ViewModel.CustomMirrorUrl);
        }

        using var g = new ToolsFixture();
        g.Settings.Update(s => s.CustomMirrorUrl = "https://ghfast.top");
        var vm = new ToolsViewModel(g.Settings, g.Paths, g.Localizer, g.FilePicker, g.Registry, g.Installer, g.Usage, g.Time, a => a());
        Assert.Equal("https://ghfast.top/", vm.SelectedMirrorPreset?.Url);
        Assert.Equal("https://ghfast.top", vm.CustomMirrorUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task TestMirrorSpeed_ReportsLatencyAndStatus(HttpStatusCode status, bool healthy)
    {
        var handler = ScriptedHandler.Status(status);
        using var f = new ToolsFixture(http: handler);
        f.ViewModel.CustomMirrorUrl = "ghproxy.net";

        await f.ViewModel.TestMirrorSpeedAsync();

        Assert.Equal(new Uri("https://ghproxy.net"), Assert.Single(handler.Requests));
        Assert.Equal(healthy, f.ViewModel.IsPingHealthy);
        Assert.Contains($"HTTP {(int)status}", f.ViewModel.PingLatencyText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task TestMirrorSpeed_BlankAddress_PingsGithub(string? address)
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.OK);
        using var f = new ToolsFixture(http: handler);
        f.ViewModel.CustomMirrorUrl = address!;

        await f.ViewModel.TestMirrorSpeedAsync();

        Assert.Equal(new Uri(ToolDownloadUrls.GithubPrefix), Assert.Single(handler.Requests));
    }

    [Theory]
    [InlineData(":::invalid-url:::")]
    [InlineData("http://   ")]
    [InlineData("htp://bad-scheme")]
    [InlineData("ftp://invalid.host.xyz")]
    public async Task TestMirrorSpeed_InvalidAddress_FailsWithoutRequest(string address)
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.OK);
        using var f = new ToolsFixture(http: handler);
        f.ViewModel.CustomMirrorUrl = address;

        await f.ViewModel.TestMirrorSpeedAsync();

        Assert.Empty(handler.Requests);
        Assert.False(f.ViewModel.IsPingHealthy);
        Assert.Equal(f.Localizer.Format("PingFailedFormat", f.Localizer["PingInvalidAddress"]), f.ViewModel.PingLatencyText);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "PingDnsFailed")]
    [InlineData(HttpRequestError.SecureConnectionError, "PingTlsFailed")]
    [InlineData(HttpRequestError.ConnectionError, "PingUnreachable")]
    public async Task TestMirrorSpeed_ConnectionFailure_ShowsLocalizedReason(HttpRequestError error, string reasonKey)
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException(error, "runtime message"));
        using var f = new ToolsFixture(http: handler);
        f.ViewModel.CustomMirrorUrl = "ghproxy.net";

        await f.ViewModel.TestMirrorSpeedAsync();

        Assert.False(f.ViewModel.IsPingHealthy);
        Assert.Equal(f.Localizer.Format("PingFailedFormat", f.Localizer[reasonKey]), f.ViewModel.PingLatencyText);
    }

    [Fact]
    public async Task TestMirrorSpeed_NoResponse_TimesOut()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("不可达");
        });
        using var f = new ToolsFixture(http: handler);
        var vm = new ToolsViewModel(f.Settings, f.Paths, f.Localizer, f.FilePicker, f.Registry, f.Installer, f.Usage, f.Time, a => a(), new HttpClient(handler))
        {
            PingTimeout = TimeSpan.FromMilliseconds(50)
        };

        await vm.TestMirrorSpeedAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(vm.IsPingHealthy);
        Assert.Equal(f.Localizer.Format("PingFailedFormat", f.Localizer["PingTimedOut"]), vm.PingLatencyText);
    }

    [Fact]
    public async Task TestMirrorSpeed_ConnectionError_ShowsFailure()
    {
        using var f = new ToolsFixture(http: new ScriptedHandler((_, _) => throw new HttpRequestException("refused")));

        await f.ViewModel.TestMirrorSpeedAsync();

        Assert.False(f.ViewModel.IsPingHealthy);
        // 运行时的英文异常消息不直接显示
        Assert.Equal(f.Localizer.Format("PingFailedFormat", f.Localizer["PingUnreachable"]), f.ViewModel.PingLatencyText);
    }

    [Theory]
    [InlineData(ToolId.ExifTool, "exiftool")]
    [InlineData(ToolId.Ffmpeg, "ffmpeg")]
    [InlineData(ToolId.HeifEnc, "heif-enc")]
    public async Task PickPath_UsableFile_WritesOnlyThatToolAndInvalidatesIt(ToolId tool, string stem)
    {
        using var f = new ToolsFixture();
        var file = f.CreateFile(stem + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        f.FilePicker.NextResult = file;
        f.ViewModel.ValidateToolExecutable = _ => true;

        await f.ViewModel.PickPathAsync(tool);

        Assert.Equal(file, f.Paths.Get(tool));
        Assert.All(Enum.GetValues<ToolId>().Where(t => t != tool), other => Assert.Null(f.Paths.Get(other)));
        Assert.Contains(tool, f.Registry.Invalidations);
        Assert.False(f.ViewModel.IsActionMessageError);
        Assert.Equal(f.Localizer.Format("ToolPathAppliedFormat", f.Installer.Manifest.Get(tool).DisplayName), f.ViewModel.ActionMessageText);
    }

    [Fact]
    public async Task PickPath_Canceled_ChangesNothing()
    {
        using var f = new ToolsFixture();
        f.FilePicker.NextResult = null;

        await f.ViewModel.PickPathAsync(ToolId.Ffmpeg);

        Assert.False(f.ViewModel.HasActionMessage);
        Assert.Null(f.Paths.Get(ToolId.Ffmpeg));
        Assert.Empty(f.Registry.Invalidations);
    }

    [Fact]
    public async Task PickPath_MissingFileOrWrongName_IsRejected()
    {
        using var f = new ToolsFixture();
        f.ViewModel.ValidateToolExecutable = _ => true;

        var missing = Path.Combine(f.Directory, "missing", "ffmpeg");
        f.FilePicker.NextResult = missing;
        await f.ViewModel.PickPathAsync(ToolId.Ffmpeg);
        Assert.True(f.ViewModel.IsActionMessageError);
        Assert.Equal(f.Localizer.Format("ToolPathInvalidFormat", missing), f.ViewModel.ActionMessageText);

        // 试运行对不认识的文件名一律放行，文件名检查必须独立拦下任意文件
        var notes = f.CreateFile("notes.txt");
        f.FilePicker.NextResult = notes;
        await f.ViewModel.PickPathAsync(ToolId.ExifTool);
        Assert.Equal(f.Localizer.Format("ToolPathInvalidFormat", notes), f.ViewModel.ActionMessageText);

        Assert.All(Enum.GetValues<ToolId>(), tool => Assert.Null(f.Paths.Get(tool)));
    }

    [Fact]
    public async Task PickPath_ExecutableThatDoesNotRun_IsRejected()
    {
        using var f = new ToolsFixture();
        var file = f.CreateFile("ffmpeg");
        f.FilePicker.NextResult = file;
        f.ViewModel.ValidateToolExecutable = _ => false;

        await f.ViewModel.PickPathAsync(ToolId.Ffmpeg);

        Assert.True(f.ViewModel.IsActionMessageError);
        Assert.Null(f.Paths.Get(ToolId.Ffmpeg));
    }
}
