using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tools;

/// <summary>
/// 应用内正在使用外部工具的地方。Windows 上被运行中的进程占用的目录无法改名，安装前必须让它们放手。
/// </summary>
public interface IToolUsage
{
    /// <summary>有转换任务在运行：任务持有 ExifTool 会话与 FFmpeg 进程，且不能中途打断。</summary>
    bool IsBusy { get; }

    event EventHandler? BusyChanged;

    /// <summary>结束随时可中断的进程（悬浮预览等），并等待它们退出。</summary>
    Task ReleaseIdleProcessesAsync(ToolId tool);
}

/// <summary>
/// ExifTool 会话只在任务与空间预估期间存在（用完即释放），因此任务运行期间禁止安装即可覆盖长期占用；
/// 预估的短暂占用若撞上替换，安装器会回滚到旧版本并报告替换失败。
/// </summary>
/// <param name="tasks">任务中心</param>
/// <param name="releaseIdle">结束可中断的工具进程并等待退出</param>
public sealed class TaskCenterToolUsage(IBackgroundWork tasks, Func<ToolId, Task> releaseIdle) : IToolUsage
{
    public bool IsBusy => tasks.IsBusy;

    public event EventHandler? BusyChanged
    {
        add => tasks.BusyChanged += value;
        remove => tasks.BusyChanged -= value;
    }

    public Task ReleaseIdleProcessesAsync(ToolId tool) => releaseIdle(tool);
}
