using Avalonia.Threading;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 把后台批处理进度转发到 UI 线程，并限制刷新频率，避免数千个小文件时 UI 被进度事件淹没。
/// </summary>
public sealed class DesktopProgressReporter(Action<BatchProgress> onReport) : IProgress<BatchProgress>
{
    private static readonly long MinIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;
    private long _lastReportTicks;

    public void Report(BatchProgress value)
    {
        var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var last = Interlocked.Read(ref _lastReportTicks);
        if (value.Completed < value.Total && now - last < MinIntervalTicks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReportTicks, now);
        Dispatcher.UIThread.Post(() => onReport(value));
    }
}
