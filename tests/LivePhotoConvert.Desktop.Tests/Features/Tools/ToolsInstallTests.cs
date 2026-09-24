using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>依赖页的安装流程：进度阶段、失败文案、取消、任务占用、平台不支持与安装后路径接管。</summary>
[Collection(ProcessStateCollection.Name)]
public class ToolsInstallTests
{
    private static ToolInstallProgress Progress(FakeToolInstaller installer, ToolInstallStage stage, long downloaded = 0, long? total = null, double speed = 0,
        ToolSourceFailure? failure = null, string? mirror = null) =>
        new(ToolId.Ffmpeg, stage, installer.Candidate(ToolId.Ffmpeg, mirror), 1, 2, downloaded, total, speed, failure);

    [Fact]
    public async Task Install_ShowsEachStageThenCompletesAndRefreshes()
    {
        const double Mb = 1024 * 1024;
        using var f = new ToolsFixture();
        var card = f.ViewModel.Ffmpeg;
        var loc = f.Localizer;
        var source = loc["ToolSourceNpmMirror"];
        var seen = new List<(string Text, double Value, bool Indeterminate)>();
        f.Installer.Script = (tool, _, progress, _) =>
        {
            Assert.True(card.IsInstalling);
            Assert.True(f.ViewModel.IsInstallingAny);
            Assert.All(f.ViewModel.Cards, c => Assert.False(c.CanStartInstall));
            seen.Add((card.ProgressText, card.ProgressValue, card.IsProgressIndeterminate));
            foreach (var report in new[]
            {
                Progress(f.Installer, ToolInstallStage.Downloading, (long)Mb, (long)(4 * Mb), 2 * Mb),
                Progress(f.Installer, ToolInstallStage.Downloading, (long)(3 * Mb)),
                Progress(f.Installer, ToolInstallStage.Verifying),
                Progress(f.Installer, ToolInstallStage.Extracting),
                Progress(f.Installer, ToolInstallStage.Probing),
                Progress(f.Installer, ToolInstallStage.Committing)
            })
            {
                progress!.Report(report);
                seen.Add((card.ProgressText, card.ProgressValue, card.IsProgressIndeterminate));
            }

            f.Registry.Infos[tool] = FakeToolRegistry.Found(tool, f.Installer.ExecutablePath(tool), "7.0", ToolCapabilities.Hdr);
            return Task.FromResult(FakeToolInstaller.Result(tool, f.Installer.ExecutablePath(tool)));
        };

        await f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Equal(
        new (string, double, bool)[]
        {
            (loc["ToolStagePreparing"], 0, true),
            (loc.Format("ToolStageDownloadingFormat", source, "1.0 / 4.0 MB · 2.0 MB/s"), 20, false),
            (loc.Format("ToolStageDownloadingFormat", source, "3.0 MB"), 20, true),
            (loc["ToolStageVerifying"], 80, false),
            (loc["ToolStageExtracting"], 85, false),
            (loc["ToolStageProbing"], 95, false),
            (loc["ToolStageCommitting"], 98, false)
        }, seen.Select(s => (s.Text, Math.Round(s.Value), s.Indeterminate)));

        Assert.False(card.IsInstalling);
        Assert.False(f.ViewModel.IsInstallingAny);
        Assert.All(f.ViewModel.Cards, c => Assert.True(c.CanStartInstall));
        Assert.False(f.ViewModel.IsActionMessageError);
        Assert.Equal(loc.Format("InstallCompletedFormat", "FFmpeg"), f.ViewModel.ActionMessageText);
        Assert.Contains(ToolId.Ffmpeg, f.Registry.Invalidations);
        Assert.True(card.IsReady);
        Assert.Equal("7.0", card.StatusText);
    }

    [Fact]
    public async Task Install_PassesMirrorAndFlushesPendingMirrorEdit()
    {
        using var f = new ToolsFixture();
        f.ViewModel.CustomMirrorUrl = "https://my.proxy/";

        await f.ViewModel.InstallAsync(ToolId.HeifEnc);

        Assert.Equal((ToolId.HeifEnc, "https://my.proxy/"), Assert.Single(f.Installer.Calls));
        Assert.Equal("https://my.proxy/", f.Settings.Current.CustomMirrorUrl);
    }

    [Fact]
    public void SourceNames_UseLocalizedManifestKeysAndShowMirrorHost()
    {
        using var f = new ToolsFixture();
        var loc = f.Localizer;
        var card = f.ViewModel.Ffmpeg;
        f.Installer.Script = (_, _, progress, _) =>
        {
            progress!.Report(Progress(f.Installer, ToolInstallStage.Downloading, mirror: "https://gh-proxy.com/"));
            return new TaskCompletionSource<ToolInstallResult>().Task;
        };

        _ = f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Equal(
            loc.Format("ToolStageDownloadingFormat", loc.Format("ToolSourceViaFormat", loc["ToolSourceNpmMirror"], "gh-proxy.com"), "0.0 MB"),
            card.ProgressText);
        foreach (var package in ToolManifest.Embedded.Tools.SelectMany(t => t.Packages))
        {
            Assert.NotEqual(package.NameKey, loc[package.NameKey]);
        }
    }

    [Theory]
    [InlineData(ToolFailureKind.Unverified, "ToolFailureUnverified")]
    [InlineData(ToolFailureKind.Network, "ToolFailureNetwork")]
    [InlineData(ToolFailureKind.Stalled, "ToolFailureStalled")]
    [InlineData(ToolFailureKind.IntegrityMismatch, "ToolFailureIntegrity")]
    [InlineData(ToolFailureKind.UnsafeArchive, "ToolFailureUnsafeArchive")]
    [InlineData(ToolFailureKind.InvalidArchive, "ToolFailureInvalidArchive")]
    [InlineData(ToolFailureKind.ProbeFailed, "ToolFailureProbe")]
    [InlineData(ToolFailureKind.CommitFailed, "ToolFailureCommit")]
    public async Task Install_Failure_ShowsLocalizedReasonPerSource(ToolFailureKind kind, string reasonKey)
    {
        using var f = new ToolsFixture();
        var loc = f.Localizer;
        var card = f.ViewModel.Ffmpeg;
        var packages = ToolManifest.Embedded.Get(ToolId.Ffmpeg).Packages;
        var github = packages.Single(p => p.GithubRelease);
        var viaMirror = new Uri("https://ghproxy.net/" + github.Url);
        var failures = new[]
        {
            new ToolSourceFailure(packages[0].Id, packages[0].NameKey, new Uri(packages[0].Url), kind, "raw"),
            new ToolSourceFailure(github.Id, github.NameKey, viaMirror, ToolFailureKind.Network, "raw")
        };
        string? sourceFailedText = null;
        f.Installer.Script = (tool, _, progress, _) =>
        {
            progress!.Report(Progress(f.Installer, ToolInstallStage.SourceFailed, failure: failures[0]));
            sourceFailedText = card.ProgressText;
            throw new ToolInstallException(tool, failures, "all failed");
        };

        await f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Equal(loc.Format("ToolStageSourceFailedFormat", loc["ToolSourceNpmMirror"], loc[reasonKey]), sourceFailedText);
        var expected = loc.Format("ToolSourceFailureFormat", loc["ToolSourceNpmMirror"], loc[reasonKey]) + "\n"
            + loc.Format("ToolSourceFailureFormat", loc.Format("ToolSourceViaFormat", loc["ToolSourceGitHub"], "ghproxy.net"), loc["ToolFailureNetwork"]);
        Assert.True(f.ViewModel.IsActionMessageError);
        Assert.Equal(loc.Format("InstallFailedFormat", "FFmpeg", expected), f.ViewModel.ActionMessageText);
        Assert.DoesNotContain("raw", f.ViewModel.ActionMessageText);
        Assert.False(card.IsInstalling);
        Assert.False(f.ViewModel.IsInstallingAny);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    public void FailureKinds_HaveDistinctTextsInBothLanguages(string language)
    {
        using var f = new ToolsFixture();
        f.Localizer.SetLanguage(language);

        var texts = Enum.GetValues<ToolFailureKind>().Select(k => ToolTexts.FailureKind(f.Localizer, k)).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.All(texts, t => Assert.False(t.StartsWith("ToolFailure", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Install_UnexpectedException_BecomesMessage()
    {
        using var f = new ToolsFixture();
        f.Installer.Script = (_, _, _, _) => throw new UnauthorizedAccessException("denied");

        var error = await Record.ExceptionAsync(() => f.ViewModel.InstallAsync(ToolId.ExifTool));

        Assert.Null(error);
        Assert.True(f.ViewModel.IsActionMessageError);
        // 异常消息只进日志，界面给出本地化原因
        Assert.Equal(f.Localizer.Format("InstallFailedFormat", "ExifTool", f.Localizer["ToolInstallUnexpected"]), f.ViewModel.ActionMessageText);
        f.Localizer.SetLanguage("en");
        Assert.Equal(f.Localizer.Format("InstallFailedFormat", "ExifTool", f.Localizer["ToolInstallUnexpected"]), f.ViewModel.ActionMessageText);
    }

    [Fact]
    public async Task Install_Cancel_StopsAndReportsCanceled()
    {
        using var f = new ToolsFixture();
        var card = f.ViewModel.ExifTool;
        var started = new TaskCompletionSource();
        f.Installer.Script = async (_, _, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("不应到达");
        };

        var install = f.ViewModel.InstallAsync(ToolId.ExifTool);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(card.IsInstalling);
        card.CancelCommand.Execute(null);
        await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(card.IsInstalling);
        Assert.False(f.ViewModel.IsInstallingAny);
        Assert.Equal(f.Localizer["InstallCanceled"], f.ViewModel.ActionMessageText);
    }

    [Fact]
    public async Task Install_WhileAnotherInstallRuns_IsIgnored()
    {
        using var f = new ToolsFixture();
        var gate = new TaskCompletionSource<ToolInstallResult>();
        f.Installer.Script = (_, _, _, _) => gate.Task;

        var first = f.ViewModel.InstallAsync(ToolId.ExifTool);
        await f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Single(f.Installer.Calls);
        Assert.False(f.ViewModel.Ffmpeg.IsInstalling);
        gate.SetResult(FakeToolInstaller.Result(ToolId.ExifTool, f.Installer.ExecutablePath(ToolId.ExifTool)));
        await first;
    }

    [Fact]
    public async Task Install_WhileTaskRuns_IsRefusedAndReenabledWhenTaskEnds()
    {
        using var f = new ToolsFixture(usageBusy: true);

        Assert.True(f.ViewModel.IsInstallBlocked);
        Assert.All(f.ViewModel.Cards, c => Assert.False(c.CanStartInstall));

        await f.ViewModel.InstallAsync(ToolId.ExifTool);

        Assert.Empty(f.Installer.Calls);
        Assert.Empty(f.Usage.Released);
        Assert.True(f.ViewModel.IsActionMessageError);
        Assert.Equal(f.Localizer["ToolInstallBlockedByTask"], f.ViewModel.ActionMessageText);

        f.Usage.IsBusy = false;
        Assert.False(f.ViewModel.IsInstallBlocked);
        Assert.All(f.ViewModel.Cards, c => Assert.True(c.CanStartInstall));
        f.Usage.IsBusy = true;
        Assert.True(f.ViewModel.IsInstallBlocked);
    }

    [Fact]
    public async Task Install_ReleasesIdleProcessesBeforeInstalling()
    {
        using var f = new ToolsFixture();
        f.Installer.Script = (tool, _, _, _) =>
        {
            Assert.Equal([ToolId.Ffmpeg], f.Usage.Released);
            return Task.FromResult(FakeToolInstaller.Result(tool, f.Installer.ExecutablePath(tool)));
        };

        await f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Single(f.Installer.Calls);
    }

    [Fact]
    public async Task Install_WaitsForReleasedProcessesToExitFirst()
    {
        using var f = new ToolsFixture();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Usage.ReleaseCompletion = _ => exited.Task;
        f.Installer.Script = (tool, _, _, _) => Task.FromResult(FakeToolInstaller.Result(tool, f.Installer.ExecutablePath(tool)));

        var install = f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        // 预览进程还没退出：安装器尚未开始，但界面已进入安装状态，不能再次点击
        Assert.Empty(f.Installer.Calls);
        Assert.True(f.ViewModel.IsInstallingAny);
        Assert.True(f.ViewModel.Ffmpeg.IsInstalling);

        exited.SetResult();
        await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(f.Installer.Calls);
    }

    [Fact]
    public async Task UnsupportedPlatform_DisablesInstallAndExplains()
    {
        using var f = new ToolsFixture(platformSupported: false, arrange: r =>
            r.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/usr/bin/ffmpeg", "6.1.1", ToolCapabilities.Hdr, recommended: null));

        var exif = f.ViewModel.ExifTool;
        Assert.True(exif.IsPlatformUnsupported);
        Assert.True(exif.ShowPlatformUnsupported);
        Assert.False(exif.ShowInstallButton);
        // 已找到的工具不需要再提示去包管理器安装
        Assert.False(f.ViewModel.Ffmpeg.ShowPlatformUnsupported);
        Assert.False(f.ViewModel.Ffmpeg.ShowInstallButton);

        await f.ViewModel.InstallAsync(ToolId.ExifTool);
        Assert.Empty(f.Installer.Calls);
    }

    [Fact]
    public async Task Install_ClearsExplicitPathPointingElsewhere()
    {
        using var f = new ToolsFixture();
        f.Paths.Set(ToolId.Ffmpeg, "/old/ffmpeg");
        f.Installer.Script = (tool, _, _, _) =>
        {
            f.Registry.Infos[tool] = FakeToolRegistry.Found(tool, f.Installer.ExecutablePath(tool), "7.0");
            return Task.FromResult(FakeToolInstaller.Result(tool, f.Installer.ExecutablePath(tool)));
        };

        await f.ViewModel.InstallAsync(ToolId.Ffmpeg);

        Assert.Null(f.Paths.Get(ToolId.Ffmpeg));
        Assert.Equal(f.Installer.ExecutablePath(ToolId.Ffmpeg), f.ViewModel.Ffmpeg.PathText);
    }

    [Fact]
    public async Task Install_WhenDiscoveryPrefersAnotherCopy_PinsInstalledPath()
    {
        using var f = new ToolsFixture(arrange: r =>
            r.Infos[ToolId.HeifEnc] = FakeToolRegistry.Found(ToolId.HeifEnc, "/app/heif-enc", "1.17.6", recommended: "1.23.4"));

        await f.ViewModel.InstallAsync(ToolId.HeifEnc);

        Assert.Equal(f.Installer.ExecutablePath(ToolId.HeifEnc), f.Paths.Get(ToolId.HeifEnc));
        Assert.Contains(ToolId.HeifEnc, f.Registry.Invalidations);
    }

    [Fact]
    public async Task LanguageSwitch_RetranslatesLastMessage()
    {
        using var f = new ToolsFixture(usageBusy: true);
        await f.ViewModel.InstallAsync(ToolId.ExifTool);
        var chinese = f.ViewModel.ActionMessageText;

        f.Localizer.SetLanguage("en");

        Assert.NotEqual(chinese, f.ViewModel.ActionMessageText);
        Assert.Equal(f.Localizer["ToolInstallBlockedByTask"], f.ViewModel.ActionMessageText);
    }
}
