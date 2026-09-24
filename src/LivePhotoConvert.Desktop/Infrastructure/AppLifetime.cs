using Avalonia.Controls;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>为安装更新而退出程序。</summary>
public interface IAppShutdown
{
    /// <summary>
    /// 走完正常的退出收尾后启动更新程序并关闭主窗口；用户在确认弹窗中选择不退出、或更新程序无法启动时返回 false，程序继续运行。
    /// </summary>
    Task<bool> ShutdownForUpdateAsync();
}

/// <summary>
/// 主窗口关闭流程：先确认并取消运行中的任务，再停止播放、保存设置、清理临时目录，最后才真正关闭。
/// </summary>
public sealed class AppLifetime(
    SettingsStore settings,
    IDialogService dialogs,
    ILocalizer localizer,
    IPlaybackControl playback,
    IReadOnlyList<IBackgroundWork> backgroundWork,
    IUpdateService updates,
    Action? flushPendingEdits = null) : IAppShutdown
{
    public static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>播放器的 FFmpeg 被结束后通常立即退出；卡住时不阻止关窗。</summary>
    public static readonly TimeSpan PlaybackStopTimeout = TimeSpan.FromSeconds(5);

    private bool _shutdownPrepared;
    private bool _closing;
    private Window? _window;

    public void Attach(Window window)
    {
        _window = window;
        window.Closing += (_, e) => OnClosing(window, e);
    }

    public async Task<bool> ShutdownForUpdateAsync()
    {
        // 与关窗收尾互斥：两者同时进行会重复取消任务、重复弹出确认
        if (_closing || _shutdownPrepared)
        {
            return false;
        }

        _closing = true;
        try
        {
            if (!await PrepareShutdownAsync(launchPendingUpdate: false))
            {
                return false;
            }

            updates.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            // 收尾已完成但程序继续运行：允许之后再次正常退出
            ErrorLogger.Log(ex, "启动更新程序");
            _shutdownPrepared = false;
            return false;
        }
        finally
        {
            _closing = false;
        }

        _window?.Close();
        return true;
    }

    /// <summary>
    /// 执行退出前的全部收尾；用户在确认弹窗中选择不退出时返回 false。
    /// </summary>
    /// <param name="launchPendingUpdate">是否启动"退出时安装"的更新；为更新而退出时由调用方自行启动带重启的更新程序</param>
    public async Task<bool> PrepareShutdownAsync(bool launchPendingUpdate = true)
    {
        var busy = backgroundWork.Where(w => w.IsBusy).ToList();
        if (busy.Count > 0)
        {
            var confirmed = await dialogs.ShowAsync(new ConfirmDialogViewModel
            {
                Title = localizer["ExitConfirmTitle"],
                Message = localizer["ExitConfirmDesc"],
                ConfirmText = localizer["ExitConfirmBtn"],
                CancelText = localizer["ConfirmDialogCancel"],
                IsDanger = true
            });
            if (!confirmed)
            {
                return false;
            }

            // 各任务并行取消，总等待不超过一个超时
            await Task.WhenAll(busy.Select(w => w.CancelAndWaitAsync(CancelTimeout)));
        }

        dialogs.CancelAll();
        try
        {
            await playback.StopAllAsync().WaitAsync(PlaybackStopTimeout);
        }
        catch (TimeoutException)
        {
            // 进程树已被结束，只是未确认退出
        }

        // 页面上尚在防抖中的输入先写入设置，再落盘
        flushPendingEdits?.Invoke();
        settings.Flush();
        if (settings.Current.AutoCleanTemp)
        {
            SafetyGuard.CleanOwnTempDirectories();
        }

        if (launchPendingUpdate)
        {
            updates.OnExiting();
        }

        _shutdownPrepared = true;
        return true;
    }

    private async void OnClosing(Window window, WindowClosingEventArgs e)
    {
        if (_shutdownPrepared)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        bool close;
        try
        {
            close = await PrepareShutdownAsync();
        }
        catch (Exception ex)
        {
            // 收尾失败不能把用户困在无法关闭的窗口里
            ErrorLogger.Log(ex, "退出收尾");
            _shutdownPrepared = true;
            close = true;
        }
        finally
        {
            _closing = false;
        }

        if (close)
        {
            window.Close();
        }
    }
}
