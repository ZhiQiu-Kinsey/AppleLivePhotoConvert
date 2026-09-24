using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Updates;

/// <summary>
/// 更新流程：启动自动检查的延迟与 24 小时节流、开关、跳过版本、不支持时的状态、失败静默，以及与运行中任务的重启协调。
/// </summary>
public sealed class UpdateCenterTests : IDisposable
{
    private static readonly AvailableUpdate Update310 = new("3.1.0", "### 新增\n- 自动更新", 12 * 1024 * 1024, false);

    private readonly FakeUpdateService _updates = new() { NextUpdate = Update310 };
    private readonly FakeBackgroundWork _work = new();
    private readonly FakeAppShutdown _shutdown = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero));
    private readonly DesktopTestHost _host;
    private readonly UpdateCenter _center;

    public UpdateCenterTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _host = new DesktopTestHost(services =>
        {
            services.AddSingleton<IUpdateService>(_updates);
            services.AddSingleton<IAppShutdown>(_shutdown);
        });
        _center = new UpdateCenter(_updates, _host.Settings, Dialogs, _host.Localizer, _host.Shell, [_work], _shutdown, _time);
    }

    private IDialogService Dialogs => _host.Get<IDialogService>();

    private UpdatePreferences Prefs => _host.Settings.Current.Updates;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AutoCheck_WaitsForDelay_ThenChecksAndShowsDialog()
    {
        var auto = _center.StartAutoCheck();
        _time.Advance(UpdateCenter.AutoCheckDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, _updates.CheckCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        var dialog = await WaitForDialogAsync();

        Assert.Equal(1, _updates.CheckCalls);
        Assert.Equal("3.1.0", dialog.NewVersion);
        Assert.Equal("3.0.3", dialog.CurrentVersion);
        Assert.Equal(_time.GetUtcNow(), Prefs.LastCheckTime);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, Prefs.LastCheckStatus);
        Assert.True(_center.HasUpdate);
        Assert.Equal(_host.Localizer.Format("UpdateAvailableTipFormat", "3.1.0"), _center.AvailableUpdateTip);

        dialog.LaterCommand.Execute(null);
        await auto.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(_center.HasUpdate, "稍后提醒不清除新版本提示");
    }

    [Theory]
    [InlineData(-23.9, false)]
    [InlineData(-24, true)]
    [InlineData(-48, true)]
    [InlineData(2, true)]
    public async Task AutoCheck_IsThrottledToOncePerDay(double hoursSinceLastCheck, bool expectCheck)
    {
        _host.Settings.Update(s =>
        {
            s.Updates.LastCheckTime = _time.GetUtcNow().AddHours(hoursSinceLastCheck);
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpToDate;
        });
        _updates.NextUpdate = null;

        Assert.Equal(expectCheck, _center.ShouldAutoCheck(_time.GetUtcNow()));
        var auto = _center.StartAutoCheck();
        _time.Advance(UpdateCenter.AutoCheckDelay);
        await auto.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(expectCheck ? 1 : 0, _updates.CheckCalls);
    }

    [Fact]
    public async Task AutoCheckSwitch_IsPersisted_AndDisablesStartupCheck()
    {
        Assert.True(_center.AutoCheck);
        _center.AutoCheck = false;
        Assert.False(Prefs.AutoCheck);

        await _center.StartAutoCheck().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, _updates.CheckCalls);

        // 手动检查不受开关影响
        Assert.True(_center.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unsupported_NeverChecks_AndExplainsWithDownloadPage()
    {
        _updates.IsSupported = false;
        _host.Settings.Update(s =>
        {
            s.Updates.LastAvailableVersion = "9.0.0";
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
        });

        await _center.StartAutoCheck().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, _updates.CheckCalls);
        Assert.False(_center.CheckNowCommand.CanExecute(null));
        Assert.False(_center.HasUpdate);
        Assert.True(_center.IsUnsupported);
        Assert.Equal(_host.Localizer["UpdateUnsupportedDesc"], _center.StatusText);

        await _center.OpenDownloadPageCommand.ExecuteAsync(null);
        Assert.Equal(LivePhotoConvert.Desktop.Models.AboutInfo.ReleasesUrl, Assert.Single(_host.Shell.Requests));
    }

    [Fact]
    public async Task SkippedVersion_IsNotShownAutomatically_ButManualCheckShowsIt()
    {
        _host.Settings.Update(s => s.Updates.SkippedVersion = "3.1.0");

        var auto = _center.StartAutoCheck();
        _time.Advance(UpdateCenter.AutoCheckDelay);
        await auto.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, _updates.CheckCalls);
        Assert.Null(Dialogs.Current);
        Assert.False(_center.HasUpdate);

        var manual = _center.CheckNowCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        Assert.Equal("3.1.0", dialog.NewVersion);
        dialog.LaterCommand.Execute(null);
        await manual.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SkipButton_PersistsVersion_AndHidesIndicator()
    {
        var manual = _center.CheckNowCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        Assert.True(_center.HasUpdate);

        dialog.SkipCommand.Execute(null);
        await manual.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("3.1.0", Prefs.SkippedVersion);
        Assert.False(_center.HasUpdate);
        Assert.Equal(0, _updates.DownloadCalls);
    }

    [Fact]
    public async Task NewerVersionThanSkipped_IsShownAgain()
    {
        _host.Settings.Update(s => s.Updates.SkippedVersion = "3.1.0");
        _updates.NextUpdate = Update310 with { Version = "3.2.0" };

        var auto = _center.StartAutoCheck();
        _time.Advance(UpdateCenter.AutoCheckDelay);
        var dialog = await WaitForDialogAsync();

        Assert.Equal("3.2.0", dialog.NewVersion);
        dialog.Cancel();
        await auto.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AutoCheckFailure_IsSilent_AndRecordedWithReason()
    {
        _updates.CheckFailure = new UpdateException(UpdateFailureKind.Network, "offline");

        var auto = _center.StartAutoCheck();
        _time.Advance(UpdateCenter.AutoCheckDelay);
        await auto.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(Dialogs.Current);
        Assert.Equal(UpdateCheckStatus.Failed, Prefs.LastCheckStatus);
        Assert.Equal(UpdateFailureKind.Network, Prefs.LastFailure);
        Assert.Contains(_host.Localizer["UpdateFailureNetwork"], _center.StatusText);
        // 失败也计入节流，避免断网时每次启动都重试
        Assert.False(_center.ShouldAutoCheck(_time.GetUtcNow()));
    }

    [Fact]
    public async Task ManualCheckFailure_ShowsLocalizedReason_AndKeepsKnownUpdate()
    {
        _host.Settings.Update(s =>
        {
            s.Updates.LastAvailableVersion = "3.1.0";
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
        });
        _updates.CheckFailure = new UpdateException(UpdateFailureKind.RateLimited, "403");

        await _center.CheckNowCommand.ExecuteAsync(null);

        Assert.Null(Dialogs.Current);
        Assert.Contains(_host.Localizer["UpdateFailureRateLimited"], _center.StatusText);
        Assert.True(_center.HasUpdate, "检查失败不清除上次发现的新版本");
        Assert.False(_center.IsChecking);
    }

    [Fact]
    public async Task UpToDate_ClearsIndicator_AndShowsResult()
    {
        _host.Settings.Update(s =>
        {
            s.Updates.LastAvailableVersion = "3.1.0";
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
        });
        _updates.NextUpdate = null;
        Assert.True(_center.HasUpdate);

        await _center.CheckNowCommand.ExecuteAsync(null);

        Assert.False(_center.HasUpdate);
        Assert.Equal(_host.Localizer.Format("UpdateLastCheckFormat", _time.GetUtcNow().DateTime, _host.Localizer["UpdateResultUpToDate"]), _center.StatusText);
    }

    [Fact]
    public void InstalledVersionNotNewer_IsNotAnUpdate()
    {
        // 更新完成后上次记录的版本就是当前版本
        _host.Settings.Update(s =>
        {
            s.Updates.LastAvailableVersion = "3.0.3";
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
        });

        Assert.False(_center.HasUpdate);
        Assert.Null(_center.AvailableVersion);
    }

    [Fact]
    public async Task ReadyWithoutTasks_RestartNow_ShutsDownForUpdate()
    {
        var dialog = await OpenAndDownloadAsync();
        Assert.True(dialog.IsReadyWithoutTasks);

        dialog.RestartNowCommand.Execute(null);

        await WaitUntilAsync(() => _shutdown.Calls == 1);
        Assert.False(_center.IsInstallScheduled);
    }

    [Fact]
    public async Task RestartRefused_FallsBackToInstallOnExit()
    {
        _shutdown.Result = false;
        var dialog = await OpenAndDownloadAsync();

        dialog.RestartNowCommand.Execute(null);

        await WaitUntilAsync(() => _center.IsInstallScheduled);
        Assert.Equal(1, _updates.ApplyOnExitCalls);
        Assert.Equal(_host.Localizer["UpdateInstallOnExitScheduled"], _center.StatusText);
    }

    [Fact]
    public async Task InstallOnNextLaunch_SchedulesApplyOnExit()
    {
        var dialog = await OpenAndDownloadAsync();

        dialog.InstallOnExitCommand.Execute(null);

        await WaitUntilAsync(() => _center.IsInstallScheduled);
        Assert.Equal(1, _updates.ApplyOnExitCalls);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task EscapeAfterDownload_InstallsOnExit()
    {
        var dialog = await OpenAndDownloadAsync();

        dialog.Cancel();

        await WaitUntilAsync(() => _center.IsInstallScheduled);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task TaskRunning_WaitForTasks_RestartsWhenTheyFinish()
    {
        _work.IsBusy = true;
        var dialog = await OpenAndDownloadAsync();
        Assert.True(dialog.IsReadyWithTasks);
        Assert.False(dialog.IsReadyWithoutTasks);

        dialog.RestartAfterTasksCommand.Execute(null);
        await WaitUntilAsync(() => _center.IsWaitingForTasks);
        Assert.Equal(0, _shutdown.Calls);
        Assert.Equal(1, _updates.ApplyOnExitCalls);
        Assert.Equal(_host.Localizer["UpdateWaitingForTasks"], _center.StatusText);

        _work.IsBusy = false;

        await WaitUntilAsync(() => _shutdown.Calls == 1);
        Assert.False(_center.IsWaitingForTasks);
    }

    [Fact]
    public async Task TaskRunning_CancelPendingRestart_InstallsOnExitInstead()
    {
        _work.IsBusy = true;
        var dialog = await OpenAndDownloadAsync();
        dialog.RestartAfterTasksCommand.Execute(null);
        await WaitUntilAsync(() => _center.IsWaitingForTasks);

        _center.CancelPendingRestartCommand.Execute(null);
        _work.IsBusy = false;

        Assert.True(_center.IsInstallScheduled);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task TaskRunning_CancelTasksAndRestart_CancelsFirst()
    {
        _work.IsBusy = true;
        var dialog = await OpenAndDownloadAsync();

        dialog.CancelTasksAndRestartCommand.Execute(null);

        await WaitUntilAsync(() => _shutdown.Calls == 1);
        Assert.Equal(1, _work.CancelCalls);
    }

    [Fact]
    public async Task TaskStartedWhileDialogOpen_RestartNowWaitsForIt()
    {
        var dialog = await OpenAndDownloadAsync();
        _work.IsBusy = true;
        Assert.True(dialog.IsReadyWithTasks, "弹窗打开期间任务开始，按钮随之切换");

        dialog.RestartNowCommand.Execute(null);

        await WaitUntilAsync(() => _center.IsWaitingForTasks);
        Assert.Equal(0, _shutdown.Calls);
    }

    [Fact]
    public async Task Download_CanBeCanceled_AndRetriedAfterFailure()
    {
        _updates.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = _center.CheckNowCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        Assert.True(dialog.IsPrompt);
        Assert.Equal(_host.Localizer.Format("UpdateSizeFullFormat", "12.0 MB"), dialog.SizeText);
        Assert.Contains(dialog.Notes, n => n.Text == "自动更新");

        dialog.UpdateNowCommand.Execute(null);
        Assert.True(dialog.IsDownloading);
        await WaitUntilAsync(() => dialog.Progress == 42);
        Assert.Equal(_host.Localizer.Format("UpdateDownloadingFormat", 42), dialog.ProgressText);

        dialog.CancelDownloadCommand.Execute(null);
        await dialog.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(dialog.IsPrompt);

        _updates.DownloadGate = null;
        _updates.DownloadFailure = new UpdateException(UpdateFailureKind.Disk, "disk full");
        dialog.UpdateNowCommand.Execute(null);
        await dialog.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(dialog.IsFailed);
        Assert.True(dialog.ShowChoiceButtons, "失败后可以重试或稍后");
        Assert.Equal(_host.Localizer["UpdateFailureDisk"], dialog.ErrorText);

        _updates.DownloadFailure = null;
        dialog.UpdateNowCommand.Execute(null);
        await dialog.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(dialog.IsReady);

        dialog.InstallOnExitCommand.Execute(null);
        await manual.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, _updates.DownloadCalls);
    }

    [Fact]
    public async Task ClosingDuringDownload_CancelsIt()
    {
        _updates.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = _center.CheckNowCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        dialog.UpdateNowCommand.Execute(null);

        dialog.Cancel();

        await dialog.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await manual.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(_center.IsInstallScheduled);
        Assert.Equal(0, _updates.ApplyOnExitCalls);
    }

    [Fact]
    public async Task ShowUpdate_ReusesCheckedUpdate_OrChecksFirst()
    {
        _host.Settings.Update(s =>
        {
            s.Updates.LastAvailableVersion = "3.1.0";
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
        });

        // 上次运行记录的新版本：本次运行还没有检查结果，先检查再弹窗
        var first = _center.ShowUpdateCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        Assert.Equal(1, _updates.CheckCalls);
        dialog.LaterCommand.Execute(null);
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var second = _center.ShowUpdateCommand.ExecuteAsync(null);
        dialog = await WaitForDialogAsync();
        Assert.Equal(1, _updates.CheckCalls);
        dialog.LaterCommand.Execute(null);
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private async Task<UpdateDialogViewModel> OpenAndDownloadAsync()
    {
        _ = _center.CheckNowCommand.ExecuteAsync(null);
        var dialog = await WaitForDialogAsync();
        dialog.UpdateNowCommand.Execute(null);
        await dialog.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(dialog.IsReady);
        return dialog;
    }

    private async Task<UpdateDialogViewModel> WaitForDialogAsync()
    {
        await WaitUntilAsync(() => Dialogs.Current is UpdateDialogViewModel);
        return (UpdateDialogViewModel)Dialogs.Current!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待状态超时");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
