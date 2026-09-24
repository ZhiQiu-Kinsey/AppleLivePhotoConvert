using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

public class AppServicesTests
{
    private sealed class FakeWork : IBackgroundWork
    {
        public bool IsBusy { get; set; }

        public event EventHandler? BusyChanged
        {
            add { }
            remove { }
        }

        public TimeSpan? CanceledWith { get; private set; }

        public Task CancelAndWaitAsync(TimeSpan timeout)
        {
            CanceledWith = timeout;
            IsBusy = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>记录停止调用；停止在 <see cref="Release"/> 完成前一直挂起。</summary>
    private sealed class PendingPlayback : IPlaybackControl
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopAllAsyncCalls { get; private set; }

        public void StopAll()
        {
        }

        public Task StopAllAsync()
        {
            StopAllAsyncCalls++;
            return Release.Task;
        }
    }

    [Fact]
    public async Task ToolUsage_ReleasingFfmpeg_WaitsForPlaybackToStop()
    {
        var playback = new PendingPlayback();
        using var host = new DesktopTestHost(services => services.AddSingleton<IPlaybackControl>(playback));
        var usage = host.Get<IToolUsage>();

        await usage.ReleaseIdleProcessesAsync(ToolId.ExifTool);
        Assert.Equal(0, playback.StopAllAsyncCalls);

        var release = usage.ReleaseIdleProcessesAsync(ToolId.Ffmpeg);
        Assert.Equal(1, playback.StopAllAsyncCalls);
        Assert.False(release.IsCompleted);

        playback.Release.SetResult();
        await release.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Build_ResolvesAllPagesAsSingletons_WithReplacedSettingsPath()
    {
        using var host = new DesktopTestHost();

        Assert.Equal(host.SettingsPath, host.Settings.FilePath);

        var shell = host.Get<ShellViewModel>();
        Assert.Same(shell, host.Get<ShellViewModel>());
        Assert.Same(host.Get<LibraryViewModel>(), shell.Library);
        Assert.Same(host.Get<InspectorViewModel>(), shell.Inspector);
        Assert.Same(host.Get<ToolsViewModel>(), shell.Tools);
        Assert.Same(host.Get<TasksViewModel>(), shell.Tasks);
        Assert.Same(host.Get<TaskCenter>(), shell.Tasks.Center);
        Assert.Same(host.Get<TaskCenter>(), shell.Inspector.Tasks);
        Assert.Same(host.Get<SettingsViewModel>(), shell.Settings);
        Assert.NotNull(host.Get<AppLifetime>());
        Assert.NotNull(host.Get<WindowPlacementTracker>());
        Assert.Same(host.Get<PlaybackService>(), host.Get<IPlaybackControl>());
        Assert.Same(host.Get<PlaybackService>(), host.Get<LibraryViewModel>().Playback);
        Assert.Same(host.FilePicker, host.Get<IFilePicker>());
    }

    [Fact]
    public void Navigator_DrivesTheFourShellPages()
    {
        using var host = new DesktopTestHost();
        var shell = host.Get<ShellViewModel>();
        Assert.Equal([AppPage.Library, AppPage.Tasks, AppPage.Tools, AppPage.Settings], Enum.GetValues<AppPage>());
        Assert.True(shell.IsLibrarySelected, "启动时显示图库");

        host.Get<INavigator>().NavigateTo(AppPage.Tasks);
        Assert.True(shell.IsTasksSelected);
        Assert.Equal(AppPage.Tasks, shell.CurrentPage);

        host.Get<TasksViewModel>().GoToLibraryCommand.Execute(null);
        Assert.True(shell.IsLibrarySelected);

        shell.NavigateCommand.Execute("Tools");
        Assert.True(shell.IsToolsSelected);

        shell.NavigateCommand.Execute("Settings");
        Assert.True(shell.IsSettingsSelected);
        Assert.False(shell.IsLibrarySelected || shell.IsTasksSelected || shell.IsToolsSelected);

        // 旧页面名与无效值不改变当前页
        shell.NavigateCommand.Execute("Convert");
        shell.NavigateCommand.Execute("Strip");
        shell.NavigateCommand.Execute(null);
        Assert.Equal(AppPage.Settings, shell.CurrentPage);
    }

    [Fact]
    public void SettingsPage_ChangesArePersistedWithoutSaveButton()
    {
        using var host = new DesktopTestHost();
        var settings = host.Get<SettingsViewModel>();

        settings.Concurrency = 7;
        settings.AutoCleanTemp = false;
        settings.NotifyOnComplete = false;

        Assert.True(settings.IsSettingsSaved);
        host.Settings.Flush();
        using var reloaded = new SettingsStore(host.SettingsPath);
        Assert.Equal(7, reloaded.Current.Concurrency);
        Assert.False(reloaded.Current.AutoCleanTemp);
        Assert.False(reloaded.Current.NotifyOnComplete);
    }

    /// <summary>旧版设置允许到 16；设置页与任务共用同一个上限，读入时即按上限显示。</summary>
    [Fact]
    public void SettingsPage_ClampsStoredConcurrencyToTheSameLimitAsJobs()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s => s.Concurrency = 16);

        var settings = host.Get<SettingsViewModel>();

        Assert.Equal(ConversionDefaults.MaxParallelism, settings.MaxConcurrency);
        Assert.Equal(ConversionDefaults.MaxParallelism, settings.Concurrency);
    }

    [Fact]
    public async Task AppLifetime_WithRunningTask_AsksBeforeCancelling()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s => s.AutoCleanTemp = false);
        var dialogs = host.Get<IDialogService>();
        var work = new FakeWork { IsBusy = true };
        var lifetime = new AppLifetime(host.Settings, dialogs, host.Localizer, host.Get<IPlaybackControl>(), [work], host.Get<IUpdateService>());

        var declined = lifetime.PrepareShutdownAsync();
        var question = Assert.IsType<ConfirmDialogViewModel>(dialogs.Current);
        Assert.True(question.IsDanger);
        question.CancelCommand.Execute(null);
        Assert.False(await declined.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(work.CanceledWith);
        Assert.True(work.IsBusy);

        var accepted = lifetime.PrepareShutdownAsync();
        Assert.IsType<ConfirmDialogViewModel>(dialogs.Current).ConfirmCommand.Execute(null);
        Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(AppLifetime.CancelTimeout, work.CanceledWith);
        Assert.True(File.Exists(host.SettingsPath), "退出前必须把防抖中的设置写盘");
    }

    [Fact]
    public async Task AppLifetime_WhenIdle_ClosesWithoutAsking()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s => s.AutoCleanTemp = false);
        var lifetime = new AppLifetime(host.Settings, host.Get<IDialogService>(), host.Localizer, host.Get<IPlaybackControl>(), [new FakeWork()], host.Get<IUpdateService>());

        Assert.True(await lifetime.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(host.Get<IDialogService>().Current);
    }

    [Fact]
    public async Task AppLifetime_Shutdown_SavesMirrorStillInDebounce()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s => s.AutoCleanTemp = false);
        var tools = host.Get<ToolsViewModel>();
        tools.CustomMirrorUrl = "https://mirror.example/";
        Assert.NotEqual("https://mirror.example/", host.Settings.Current.CustomMirrorUrl);

        Assert.True(await host.Get<AppLifetime>().PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal("https://mirror.example/", host.Settings.Current.CustomMirrorUrl);
        Assert.Contains("https://mirror.example/", File.ReadAllText(host.SettingsPath));
    }
}
