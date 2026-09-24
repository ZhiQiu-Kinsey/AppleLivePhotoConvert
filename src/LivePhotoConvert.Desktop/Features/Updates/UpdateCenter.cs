using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using Velopack;

namespace LivePhotoConvert.Desktop.Features.Updates;

/// <summary>
/// 更新的状态与流程：启动后自动检查（每天最多一次）、手动检查、新版本弹窗、下载后重启或退出时安装，
/// 以及与运行中任务的协调。设置页、侧栏与关于页共用这一个实例。
/// </summary>
public sealed partial class UpdateCenter : ObservableObject
{
    /// <summary>启动后等待片刻再检查，不与首屏扫描、缩略图争用网络与磁盘。</summary>
    public static readonly TimeSpan AutoCheckDelay = TimeSpan.FromSeconds(10);

    /// <summary>自动检查的最短间隔。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IUpdateService _updates;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IShellLauncher _shell;
    private readonly IReadOnlyList<IBackgroundWork> _work;
    private readonly IAppShutdown _shutdown;
    private readonly TimeProvider _time;

    /// <summary>本次运行中最近一次检查得到的新版本；弹窗与下载都需要它。</summary>
    private AvailableUpdate? _available;
    private bool _dialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    /// <summary>更新已下载，等运行中的任务结束后自动重启安装。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isWaitingForTasks;

    /// <summary>更新已下载，退出程序时安装。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isInstallScheduled;

    [ObservableProperty]
    private bool _autoCheck;

    public UpdateCenter(IUpdateService updates, SettingsStore settings, IDialogService dialogs, ILocalizer localizer, IShellLauncher shell,
        IReadOnlyList<IBackgroundWork> work, IAppShutdown shutdown, TimeProvider? time = null)
    {
        _updates = updates;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _shell = shell;
        _work = work;
        _shutdown = shutdown;
        _time = time ?? TimeProvider.System;
        _autoCheck = settings.Current.Updates.AutoCheck;
        _localizer.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AvailableUpdateTip));
        };
    }

    public bool IsSupported => _updates.IsSupported;

    public bool IsUnsupported => !_updates.IsSupported;

    public string CurrentVersion => _updates.CurrentVersion;

    /// <summary>最近一次检查发现、且比当前版本新的版本；没有时为 null。</summary>
    public string? AvailableVersion =>
        IsSupported && _settings.Current.Updates.LastAvailableVersion is { Length: > 0 } version && IsNewer(version) ? version : null;

    /// <summary>有未跳过的新版本：侧栏"设置"显示小圆点，关于页显示"有新版本"。</summary>
    public bool HasUpdate => AvailableVersion is { } version && !IsSkipped(version);

    public string AvailableUpdateTip => AvailableVersion is { } version ? _localizer.Format("UpdateAvailableTipFormat", version) : string.Empty;

    /// <summary>设置页"更新"分组的状态行。</summary>
    public string StatusText
    {
        get
        {
            if (!IsSupported)
            {
                return _localizer["UpdateUnsupportedDesc"];
            }

            if (IsChecking)
            {
                return _localizer["UpdateChecking"];
            }

            if (IsWaitingForTasks)
            {
                return _localizer["UpdateWaitingForTasks"];
            }

            if (IsInstallScheduled)
            {
                return _localizer["UpdateInstallOnExitScheduled"];
            }

            var prefs = _settings.Current.Updates;
            if (prefs.LastCheckTime is not { } last || prefs.LastCheckStatus == UpdateCheckStatus.Never)
            {
                return _localizer["UpdateNeverChecked"];
            }

            var result = prefs.LastCheckStatus switch
            {
                UpdateCheckStatus.UpdateAvailable when AvailableVersion is { } version => _localizer.Format("UpdateResultAvailableFormat", version),
                UpdateCheckStatus.Failed => _localizer.Format("UpdateResultFailedFormat", UpdateTexts.Failure(_localizer, prefs.LastFailure)),
                _ => _localizer["UpdateResultUpToDate"]
            };
            return _localizer.Format("UpdateLastCheckFormat", TimeZoneInfo.ConvertTime(last, _time.LocalTimeZone).DateTime, result);
        }
    }

    /// <summary>最近一次自动检查流程（含等待与弹窗）；测试据此等待。</summary>
    public Task AutoCheckTask { get; private set; } = Task.CompletedTask;

    /// <summary>启动时调用：开启自动检查时，延迟 <see cref="AutoCheckDelay"/> 后在后台检查，距上次检查不足 <see cref="CheckInterval"/> 则跳过。</summary>
    public Task StartAutoCheck()
    {
        AutoCheckTask = RunAutoCheckAsync();
        return AutoCheckTask;
    }

    /// <summary>自动检查的节流条件；时钟被调回（上次检查时间在未来）时视为已过期。</summary>
    public bool ShouldAutoCheck(DateTimeOffset now)
    {
        var prefs = _settings.Current.Updates;
        if (!IsSupported || !prefs.AutoCheck)
        {
            return false;
        }

        return prefs.LastCheckTime is not { } last || now - last >= CheckInterval || last > now;
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        // 手动检查总是弹出结果，跳过的版本同样显示
        if (await CheckCoreAsync(manual: true) is { } update)
        {
            await ShowDialogAsync(update);
        }
    }

    private bool CanCheckNow() => IsSupported && !IsChecking;

    /// <summary>侧栏小圆点、关于页与设置页的"查看更新"：本次运行已检查过就直接弹窗，否则先检查。</summary>
    [RelayCommand]
    private async Task ShowUpdateAsync()
    {
        if (_available is { } update && update.Version == AvailableVersion)
        {
            await ShowDialogAsync(update);
        }
        else if (CanCheckNow())
        {
            await CheckNowAsync();
        }
    }

    [RelayCommand]
    private async Task OpenDownloadPageAsync()
    {
        if (!_shell.OpenUri(AboutInfo.ReleasesUrl))
        {
            await _dialogs.AlertAsync(_localizer["ShellOpenFailedTitle"], _localizer.Format("ShellOpenFailedFormat", AboutInfo.ReleasesUrl), _localizer["ConfirmDialogOk"]);
        }
    }

    /// <summary>放弃"任务完成后重启"，改为退出时安装。</summary>
    [RelayCommand]
    private void CancelPendingRestart()
    {
        StopWaitingForTasks();
        IsInstallScheduled = true;
    }

    partial void OnAutoCheckChanged(bool value) => _settings.Update(s => s.Updates.AutoCheck = value);

    private async Task RunAutoCheckAsync()
    {
        if (!ShouldAutoCheck(_time.GetUtcNow()))
        {
            return;
        }

        await Task.Delay(AutoCheckDelay, _time);
        // 等待期间可能关闭了开关或已手动检查
        if (IsChecking || !ShouldAutoCheck(_time.GetUtcNow()))
        {
            return;
        }

        if (await CheckCoreAsync(manual: false) is { } update && !IsSkipped(update.Version))
        {
            await ShowDialogAsync(update);
        }
    }

    private async Task<AvailableUpdate?> CheckCoreAsync(bool manual)
    {
        IsChecking = true;
        try
        {
            var update = await _updates.CheckAsync();
            _available = update;
            Record(update is null ? UpdateCheckStatus.UpToDate : UpdateCheckStatus.UpdateAvailable, update?.Version ?? string.Empty, default);
            return update;
        }
        catch (Exception ex)
        {
            // 自动检查失败只记日志与状态行，不打扰；手动检查的原因显示在设置页状态行
            ErrorLogger.Log(ex, manual ? "检查更新" : "自动检查更新");
            Record(UpdateCheckStatus.Failed, null, ex is UpdateException failure ? failure.Kind : UpdateFailureKind.Unknown);
            return null;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <param name="availableVersion">null 表示保留上次发现的版本（检查失败时不改变已知结果）</param>
    private void Record(UpdateCheckStatus status, string? availableVersion, UpdateFailureKind failure)
    {
        _settings.Update(s =>
        {
            s.Updates.LastCheckTime = _time.GetUtcNow();
            s.Updates.LastCheckStatus = status;
            s.Updates.LastFailure = failure;
            if (availableVersion is not null)
            {
                s.Updates.LastAvailableVersion = availableVersion;
            }
        });
        NotifyAvailability();
    }

    private async Task ShowDialogAsync(AvailableUpdate update)
    {
        if (_dialogOpen || IsWaitingForTasks)
        {
            return;
        }

        _dialogOpen = true;
        UpdateDialogResult result;
        try
        {
            result = await _dialogs.ShowAsync(new UpdateDialogViewModel(_updates, update, _localizer, _work));
        }
        finally
        {
            _dialogOpen = false;
        }

        switch (result)
        {
            case UpdateDialogResult.Skip:
                _settings.Update(s => s.Updates.SkippedVersion = update.Version);
                NotifyAvailability();
                break;
            case UpdateDialogResult.InstallOnExit:
                _updates.ApplyOnExit();
                IsInstallScheduled = true;
                break;
            case UpdateDialogResult.RestartNow:
                await RestartAsync();
                break;
            case UpdateDialogResult.RestartAfterTasks:
                WaitForTasksThenRestart();
                break;
            case UpdateDialogResult.CancelTasksAndRestart:
                await Task.WhenAll(_work.Where(w => w.IsBusy).Select(w => w.CancelAndWaitAsync(AppLifetime.CancelTimeout)));
                await RestartAsync();
                break;
        }
    }

    private void WaitForTasksThenRestart()
    {
        // 等待期间用户自行退出时同样完成安装
        _updates.ApplyOnExit();
        if (!_work.Any(w => w.IsBusy))
        {
            _ = RestartAsync();
            return;
        }

        IsWaitingForTasks = true;
        foreach (var work in _work)
        {
            work.BusyChanged += OnWorkBusyChanged;
        }
    }

    private void OnWorkBusyChanged(object? sender, EventArgs e)
    {
        if (_work.Any(w => w.IsBusy))
        {
            return;
        }

        StopWaitingForTasks();
        _ = RestartAsync();
    }

    private void StopWaitingForTasks()
    {
        foreach (var work in _work)
        {
            work.BusyChanged -= OnWorkBusyChanged;
        }

        IsWaitingForTasks = false;
    }

    private async Task RestartAsync()
    {
        bool restarted;
        try
        {
            restarted = await _shutdown.ShutdownForUpdateAsync();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "重启以安装更新");
            restarted = false;
        }

        if (!restarted)
        {
            // 用户在退出确认中选择继续使用，或更新程序无法启动：退出时再安装
            _updates.ApplyOnExit();
            IsInstallScheduled = true;
        }
    }

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(AvailableUpdateTip));
        OnPropertyChanged(nameof(StatusText));
    }

    private bool IsSkipped(string version) =>
        string.Equals(_settings.Current.Updates.SkippedVersion, version, StringComparison.OrdinalIgnoreCase);

    private bool IsNewer(string version) =>
        SemanticVersion.TryParse(version, out var candidate)
        && (!SemanticVersion.TryParse(CurrentVersion, out var current) || candidate > current);
}
