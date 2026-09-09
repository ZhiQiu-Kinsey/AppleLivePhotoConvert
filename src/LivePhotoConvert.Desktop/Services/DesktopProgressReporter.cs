using LivePhotoConvert.Core.Abstractions;

namespace LivePhotoConvert.Desktop.Services;

public sealed class DesktopProgressReporter(Action<int, int, string> onReport) : IProgressReporter
{
    public void Report(int completed, int total, string currentItem) =>
        onReport(completed, total, currentItem);
}
