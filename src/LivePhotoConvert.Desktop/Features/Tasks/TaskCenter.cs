using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>
/// 全应用唯一的任务执行入口：同一时刻只运行一个任务，完成后生成报告、写入历史并切换到任务页。
/// 成员须在界面线程调用。
/// </summary>
public sealed partial class TaskCenter : ObservableObject, IBackgroundWork
{
    private readonly IConversionRunner _runner;
    private readonly ILocalizer _localizer;
    private readonly INavigator _navigator;
    private readonly CompletionEffects _completion;
    private readonly IShellLauncher _shell;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _filePicker;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim? _pauseGate;
    private Task<TaskReportViewModel>? _running;

    /// <param name="runner">执行转换</param>
    /// <param name="localizer">文案</param>
    /// <param name="navigator">完成后切换到任务页</param>
    /// <param name="completion">完成提示音与自动打开目录</param>
    /// <param name="shell">报告中的打开目录与定位文件</param>
    /// <param name="dialogs">报告中的失败提示</param>
    /// <param name="filePicker">报告导出位置</param>
    /// <param name="time">计时；测试可注入可控时钟</param>
    /// <param name="post">把进度投递到界面线程；默认使用 Avalonia 调度器</param>
    public TaskCenter(
        IConversionRunner runner,
        ILocalizer localizer,
        INavigator navigator,
        CompletionEffects completion,
        IShellLauncher shell,
        IDialogService dialogs,
        IFilePicker filePicker,
        TimeProvider? time = null,
        Action<Action>? post = null)
    {
        _runner = runner;
        _localizer = localizer;
        _navigator = navigator;
        _completion = completion;
        _shell = shell;
        _dialogs = dialogs;
        _filePicker = filePicker;
        _time = time ?? TimeProvider.System;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _localizer.LanguageChanged += (_, _) => _post(RefreshTexts);
    }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private RunningTaskViewModel? _current;

    /// <summary>本次会话的报告，最新在前。</summary>
    public ObservableCollection<TaskReportViewModel> History { get; } = [];

    /// <summary>报告已写入历史之后触发，取消与失败的任务同样触发。</summary>
    public event EventHandler<TaskReportViewModel>? Completed;

    bool IBackgroundWork.IsBusy => IsRunning;

    /// <summary>
    /// 启动任务并在结束后返回报告；已有任务运行时直接拒绝，调用方应以 <see cref="IsRunning"/> 控制入口可用性。
    /// </summary>
    /// <exception cref="InvalidOperationException">已有任务在运行</exception>
    public Task<TaskReportViewModel> RunAsync(ConversionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (IsRunning)
        {
            throw new InvalidOperationException("已有任务在运行。");
        }

        var cts = new CancellationTokenSource();
        var gate = new ManualResetEventSlim(initialState: true);
        var running = new RunningTaskViewModel(job, _localizer, _time);
        _cts = cts;
        _pauseGate = gate;
        Current = running;
        IsRunning = true;

        var task = RunCoreAsync(job, running, cts, gate);
        _running = task;
        return task;
    }

    public void Cancel()
    {
        if (_cts is not { } cts || Current is not { } running)
        {
            return;
        }

        running.IsCancelling = true;
        running.SetPaused(false);
        // 不打开暂停闸门：被阻塞的工作线程由取消令牌唤醒并抛出取消，打开闸门反而可能让它继续领取下一项
        cts.Cancel();
    }

    public void TogglePause()
    {
        if (_pauseGate is not { } gate || Current is not { IsCancelling: false } running)
        {
            return;
        }

        if (running.IsPaused)
        {
            gate.Set();
            running.SetPaused(false);
        }
        else
        {
            gate.Reset();
            running.SetPaused(true);
        }
    }

    /// <summary>请求取消并最多等待 <paramref name="timeout"/>；超时后不再等待，任务在后台继续收尾。</summary>
    public async Task CancelAndWaitAsync(TimeSpan timeout)
    {
        if (_running is not { IsCompleted: false } task)
        {
            return;
        }

        Cancel();
        await Task.WhenAny(task, Task.Delay(timeout, _time));
    }

    private async Task<TaskReportViewModel> RunCoreAsync(ConversionJob job, RunningTaskViewModel running, CancellationTokenSource cts, ManualResetEventSlim gate)
    {
        var startedAt = _time.GetLocalNow();
        BatchReport? report = null;
        string? error = null;
        try
        {
            if (job.ItemCount == 0)
            {
                error = EmptyInputMessage(job.Action);
            }
            else
            {
                var progress = new PausableProgress(new DesktopProgressReporter(running.Report, _post), gate, cts.Token);
                report = await Task.Run(() => _runner.RunAsync(job, progress, cts.Token), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Core 通常自行把取消转成部分报告；读取元数据等前置阶段被取消时在这里补一个空报告
            report = new BatchReport([], _time.GetLocalNow() - startedAt, Canceled: true);
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, TaskTexts.Title(_localizer, job.Action));
            error = ex.Message;
        }
        finally
        {
            _cts = null;
            _pauseGate = null;
            _running = null;
            Current = null;
            IsRunning = false;
            cts.Dispose();
            gate.Dispose();
        }

        var result = new TaskReportViewModel(this, job, report, error, startedAt, _time.GetLocalNow(), _localizer, _shell, _dialogs, _filePicker);
        History.Insert(0, result);
        _navigator.NavigateTo(AppPage.Tasks);
        if (!result.WasCanceled)
        {
            _completion.RunOnTaskComplete(job.ResultLocation);
        }

        Completed?.Invoke(this, result);
        return result;
    }

    private string EmptyInputMessage(ConversionAction action) => _localizer[action switch
    {
        ConversionAction.ToAndroid => "NoMergePairs",
        ConversionAction.Strip => "NoAlbumOrPhotoSelected",
        _ => "NoSplitCandidates"
    }];

    private void RefreshTexts()
    {
        Current?.Refresh();
        foreach (var report in History)
        {
            report.Refresh();
        }
    }
}
