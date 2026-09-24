using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

public enum UpdateDialogResult
{
    /// <summary>稍后提醒：本次不更新。</summary>
    Later,

    /// <summary>跳过此版本：不再自动提醒。</summary>
    Skip,

    /// <summary>下载完成，立即重启安装。</summary>
    RestartNow,

    /// <summary>下载完成，等运行中的任务结束后自动重启安装。</summary>
    RestartAfterTasks,

    /// <summary>下载完成，取消运行中的任务并立即重启安装。</summary>
    CancelTasksAndRestart,

    /// <summary>下载完成，退出程序时安装。</summary>
    InstallOnExit
}

public enum UpdateDialogStage
{
    Prompt,
    Downloading,
    Ready,
    Failed
}

/// <summary>
/// 发现新版本：展示版本对比、更新说明与下载大小；"立即更新"在弹窗内下载（可取消），完成后选择重启安装或退出时安装。
/// </summary>
public sealed partial class UpdateDialogViewModel : DialogViewModel<UpdateDialogResult>
{
    private readonly IUpdateService _updates;
    private readonly AvailableUpdate _update;
    private readonly ILocalizer _localizer;
    private readonly IReadOnlyList<IBackgroundWork> _work;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrompt), nameof(IsDownloading), nameof(IsReady), nameof(IsFailed), nameof(IsReadyWithoutTasks), nameof(IsReadyWithTasks), nameof(ShowChoiceButtons))]
    private UpdateDialogStage _stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyWithoutTasks), nameof(IsReadyWithTasks))]
    private bool _areTasksRunning;

    public UpdateDialogViewModel(IUpdateService updates, AvailableUpdate update, ILocalizer localizer, IReadOnlyList<IBackgroundWork> work)
    {
        _updates = updates;
        _update = update;
        _localizer = localizer;
        _work = work;
        Notes = ReleaseNotes.Parse(update.NotesMarkdown);
        foreach (var item in _work)
        {
            item.BusyChanged += OnWorkBusyChanged;
        }

        _areTasksRunning = _work.Any(w => w.IsBusy);
    }

    public string Title => _localizer.Format("UpdateDialogTitleFormat", _update.Version);

    public string CurrentVersion => _updates.CurrentVersion;

    public string NewVersion => _update.Version;

    public string SizeText => UpdateTexts.Size(_localizer, _update);

    public IReadOnlyList<ReleaseNoteBlock> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    public bool IsPrompt => Stage == UpdateDialogStage.Prompt;

    public bool IsDownloading => Stage == UpdateDialogStage.Downloading;

    public bool IsReady => Stage == UpdateDialogStage.Ready;

    public bool IsFailed => Stage == UpdateDialogStage.Failed;

    /// <summary>提示阶段与失败后都显示"立即更新 / 稍后提醒 / 跳过此版本"。</summary>
    public bool ShowChoiceButtons => Stage is UpdateDialogStage.Prompt or UpdateDialogStage.Failed;

    public bool IsReadyWithoutTasks => IsReady && !AreTasksRunning;

    public bool IsReadyWithTasks => IsReady && AreTasksRunning;

    public string ProgressText => _localizer.Format("UpdateDownloadingFormat", Progress);

    /// <summary>当前下载；测试据此等待。</summary>
    public Task DownloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>下载完成后按 Esc 或点遮罩：更新已就绪，退出时安装，避免下次启动时的"意外"更新无从解释。</summary>
    public override object? CancelResult => IsReady ? UpdateDialogResult.InstallOnExit : UpdateDialogResult.Later;

    [RelayCommand]
    private void UpdateNow()
    {
        if (IsDownloading || IsClosed)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        Progress = 0;
        ErrorText = string.Empty;
        Stage = UpdateDialogStage.Downloading;
        DownloadTask = DownloadAsync(cts);
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private void Later() => Close(UpdateDialogResult.Later);

    [RelayCommand]
    private void Skip() => Close(UpdateDialogResult.Skip);

    [RelayCommand]
    private void RestartNow() => Close(AreTasksRunning ? UpdateDialogResult.RestartAfterTasks : UpdateDialogResult.RestartNow);

    [RelayCommand]
    private void RestartAfterTasks() => Close(UpdateDialogResult.RestartAfterTasks);

    [RelayCommand]
    private void CancelTasksAndRestart() => Close(UpdateDialogResult.CancelTasksAndRestart);

    [RelayCommand]
    private void InstallOnExit() => Close(UpdateDialogResult.InstallOnExit);

    protected internal override void OnClosed()
    {
        _downloadCts?.Cancel();
        foreach (var item in _work)
        {
            item.BusyChanged -= OnWorkBusyChanged;
        }
    }

    private async Task DownloadAsync(CancellationTokenSource cts)
    {
        // Progress<T> 在创建它的界面线程上回报
        var progress = new Progress<int>(value =>
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                Progress = Math.Clamp(value, 0, 100);
            }
        });

        try
        {
            await _updates.DownloadAsync(_update, progress, cts.Token);
            if (!IsClosed && ReferenceEquals(_downloadCts, cts))
            {
                Progress = 100;
                AreTasksRunning = _work.Any(w => w.IsBusy);
                Stage = UpdateDialogStage.Ready;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!IsClosed && ReferenceEquals(_downloadCts, cts))
            {
                Stage = UpdateDialogStage.Prompt;
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, "下载更新");
            if (!IsClosed && ReferenceEquals(_downloadCts, cts))
            {
                ErrorText = UpdateTexts.Failure(_localizer, ex);
                Stage = UpdateDialogStage.Failed;
            }
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
            }

            cts.Dispose();
        }
    }

    private void OnWorkBusyChanged(object? sender, EventArgs e) => AreTasksRunning = _work.Any(w => w.IsBusy);
}
