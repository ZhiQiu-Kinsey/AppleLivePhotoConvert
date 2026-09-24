using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage.DesktopInfrastructure;

public class AppServicesTests
{
    private sealed class FakeWork : IBackgroundWork
    {
        public bool IsBusy { get; set; }

        public TimeSpan? CanceledWith { get; private set; }

        public Task CancelAndWaitAsync(TimeSpan timeout)
        {
            CanceledWith = timeout;
            IsBusy = false;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Build_ResolvesAllPagesAsSingletons_WithReplacedSettingsPath()
    {
        using var host = new DesktopTestHost();

        Assert.Equal(host.SettingsPath, host.Settings.FilePath);

        var shell = host.Get<MainWindowViewModel>();
        Assert.Same(shell, host.Get<MainWindowViewModel>());
        Assert.Same(host.Get<ConvertViewModel>(), shell.ConvertVm);
        Assert.Same(host.Get<StripViewModel>(), shell.StripVm);
        Assert.Same(host.Get<ToolsViewModel>(), shell.ToolsVm);
        Assert.Same(host.Get<ReportViewModel>(), shell.ReportVm);
        Assert.Same(host.Get<SettingsViewModel>(), shell.SettingsVm);
        Assert.NotNull(host.Get<AppLifetime>());
        Assert.NotNull(host.Get<WindowPlacementTracker>());
        Assert.Same(PlaybackHost.Instance, host.Get<PlaybackHost>());
        Assert.Same(host.FilePicker, host.Get<IFilePicker>());
    }

    [Fact]
    public void Navigator_DrivesMainWindowTabs()
    {
        using var host = new DesktopTestHost();
        var shell = host.Get<MainWindowViewModel>();

        host.Get<INavigator>().NavigateTo(AppPage.Report);
        Assert.True(shell.IsReportTabSelected);
        Assert.Equal((int)AppPage.Report, shell.SelectedTabIndex);

        host.Get<ReportViewModel>().ReturnToConvertCommand.Execute(null);
        Assert.True(shell.IsConvertTabSelected);

        shell.SelectTabCommand.Execute("4");
        Assert.True(shell.IsSettingsTabSelected);
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

    [Fact]
    public void ConvertPage_ParametersAreStoredInSettings()
    {
        using var host = new DesktopTestHost();
        var convert = host.Get<ConvertViewModel>();

        convert.HeicQuality = 72;
        convert.AutoAppendIndex = false;
        convert.NamingFormat = 2;
        convert.KeepSubfolderHierarchy = false;

        var current = host.Settings.Current;
        Assert.Equal(72, current.HeicQuality);
        Assert.Equal(Core.Pipeline.ConflictPolicy.Overwrite, current.ConflictPolicy);
        Assert.Equal(2, current.NamingFormat);
        Assert.False(current.KeepSubfolderHierarchy);
    }

    [Fact]
    public async Task ShellFailure_IsReportedThroughDialog()
    {
        using var host = new DesktopTestHost();
        host.Shell.Result = false;
        var report = host.Get<ReportViewModel>();
        report.Populate(new BatchReportModel { SummaryBadge = "x", Records = [], OutputDirectory = host.Directory });

        var open = report.OpenOutputDirCommand.ExecuteAsync(null);

        var alert = Assert.IsType<ConfirmDialogViewModel>(host.Get<IDialogService>().Current);
        Assert.True(alert.IsSingleButton);
        Assert.Contains(host.Directory, alert.Message);
        alert.ConfirmCommand.Execute(null);
        await open.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([host.Directory], host.Shell.Requests);
    }

    [Fact]
    public async Task AppLifetime_WithRunningTask_AsksBeforeCancelling()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s => s.AutoCleanTemp = false);
        var dialogs = host.Get<IDialogService>();
        var work = new FakeWork { IsBusy = true };
        var lifetime = new AppLifetime(host.Settings, dialogs, host.Localizer, PlaybackHost.Instance, [work]);

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
        var lifetime = new AppLifetime(host.Settings, host.Get<IDialogService>(), host.Localizer, PlaybackHost.Instance, [new FakeWork()]);

        Assert.True(await lifetime.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(host.Get<IDialogService>().Current);
    }
}
