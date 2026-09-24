using Avalonia.Controls;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 主窗口关闭流程：先确认并取消运行中的任务，再停止播放、保存设置、清理临时目录，最后才真正关闭。
/// </summary>
public sealed class AppLifetime(
    SettingsStore settings,
    IDialogService dialogs,
    ILocalizer localizer,
    IPlaybackControl playback,
    IReadOnlyList<IBackgroundWork> backgroundWork,
    Action? flushPendingEdits = null)
{
    public static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>播放器的 FFmpeg 被结束后通常立即退出；卡住时不阻止关窗。</summary>
    public static readonly TimeSpan PlaybackStopTimeout = TimeSpan.FromSeconds(5);

    private bool _shutdownPrepared;
    private bool _closing;

    public void Attach(Window window) => window.Closing += (_, e) => OnClosing(window, e);

    /// <summary>
    /// 执行退出前的全部收尾；用户在确认弹窗中选择不退出时返回 false。
    /// </summary>
    public async Task<bool> PrepareShutdownAsync()
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
