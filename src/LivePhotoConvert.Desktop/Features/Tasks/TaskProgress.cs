using Avalonia.Threading;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>
/// 把后台批处理进度转发到 UI 线程，并限制刷新频率，避免数千个小文件时 UI 被进度事件淹没。
/// </summary>
/// <param name="onReport">在 UI 线程执行的回调</param>
/// <param name="post">投递到 UI 线程的方式；默认使用 Avalonia 调度器</param>
public sealed class DesktopProgressReporter(Action<BatchProgress> onReport, Action<Action>? post = null) : IProgress<BatchProgress>
{
    private static readonly long MinIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;
    private readonly Action<Action> _post = post ?? (action => Dispatcher.UIThread.Post(action));
    private long _lastReportTicks;

    public void Report(BatchProgress value)
    {
        var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var last = Interlocked.Read(ref _lastReportTicks);
        // 最后一项必须送达，否则界面会停在 99%
        if (value.Completed < value.Total && now - last < MinIntervalTicks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReportTicks, now);
        _post(() => onReport(value));
    }
}

/// <summary>
/// 在条目之间阻塞工作线程实现暂停：Core 在每个条目完成后同步汇报进度，阻塞汇报即阻止该线程领取下一项。
/// </summary>
public sealed class PausableProgress(IProgress<BatchProgress> inner, ManualResetEventSlim gate, CancellationToken cancellationToken) : IProgress<BatchProgress>
{
    public void Report(BatchProgress value)
    {
        inner.Report(value);
        gate.Wait(cancellationToken);
        // 闸门打开与取消同时发生时 Wait 可能正常返回，这里再确认一次
        cancellationToken.ThrowIfCancellationRequested();
    }
}
