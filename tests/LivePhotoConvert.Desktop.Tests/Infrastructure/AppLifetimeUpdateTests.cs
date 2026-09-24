using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Updates;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

/// <summary>退出流程与更新的协调：正常退出时启动"退出时安装"，为更新而退出时先走完收尾再启动带重启的更新程序。</summary>
public sealed class AppLifetimeUpdateTests : IDisposable
{
    private readonly DesktopTestHost _host = new();
    private readonly FakeUpdateService _updates = new();
    private readonly FakeBackgroundWork _work = new();
    private readonly AppLifetime _lifetime;

    public AppLifetimeUpdateTests()
    {
        _host.Settings.Update(s => s.AutoCleanTemp = false);
        _lifetime = new AppLifetime(_host.Settings, _host.Get<IDialogService>(), _host.Localizer, _host.Get<IPlaybackControl>(), [_work], _updates);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task NormalExit_LaunchesPendingInstall()
    {
        Assert.True(await _lifetime.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(1, _updates.OnExitingCalls);
        Assert.Equal(0, _updates.ApplyAndRestartCalls);
    }

    [Fact]
    public async Task ShutdownForUpdate_FinishesCleanupThenStartsUpdater()
    {
        _host.Settings.Update(s => s.Concurrency = 5);

        Assert.True(await _lifetime.ShutdownForUpdateAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(1, _updates.ApplyAndRestartCalls);
        Assert.Equal(0, _updates.OnExitingCalls);
        Assert.True(File.Exists(_host.SettingsPath), "启动更新程序前必须把设置写盘");
        // 已进入退出流程，不再重复
        Assert.False(await _lifetime.ShutdownForUpdateAsync());
    }

    [Fact]
    public async Task ShutdownForUpdate_DeclinedWhileTaskRuns_KeepsRunning()
    {
        _work.IsBusy = true;
        var dialogs = _host.Get<IDialogService>();

        var pending = _lifetime.ShutdownForUpdateAsync();
        Assert.IsType<ConfirmDialogViewModel>(dialogs.Current).CancelCommand.Execute(null);

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, _updates.ApplyAndRestartCalls);
        Assert.Equal(0, _work.CancelCalls);
    }

    [Fact]
    public async Task UpdaterFailingToStart_LeavesAppUsable()
    {
        _updates.ApplyAndRestartFailure = new InvalidOperationException("Update.exe missing");

        Assert.False(await _lifetime.ShutdownForUpdateAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // 之后仍可正常退出，并尝试退出时安装
        Assert.True(await _lifetime.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, _updates.OnExitingCalls);
    }
}
