using System.ComponentModel;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Features.Tools;

/// <summary>
/// 应用内正在使用外部工具的地方。Windows 上被运行中的进程占用的目录无法改名，安装前必须让它们放手。
/// </summary>
public interface IToolUsage
{
    /// <summary>有转换任务在运行：任务持有 ExifTool 会话与 FFmpeg 进程，且不能中途打断。</summary>
    bool IsBusy { get; }

    event EventHandler? BusyChanged;

    /// <summary>结束随时可中断的进程（悬浮预览等）。</summary>
    void ReleaseIdleProcesses(ToolId tool);
}

/// <summary>
/// ExifTool 会话只在任务与空间预估期间存在（用完即释放），因此任务运行期间禁止安装即可覆盖长期占用；
/// 预估的短暂占用若撞上替换，安装器会回滚到旧版本并报告替换失败。
/// </summary>
public sealed class TaskCenterToolUsage : IToolUsage
{
    private readonly TaskCenter _tasks;
    private readonly Action<ToolId> _releaseIdle;

    /// <param name="tasks">任务中心</param>
    /// <param name="releaseIdle">结束可中断的工具进程</param>
    public TaskCenterToolUsage(TaskCenter tasks, Action<ToolId> releaseIdle)
    {
        _tasks = tasks;
        _releaseIdle = releaseIdle;
        _tasks.PropertyChanged += OnTasksChanged;
    }

    public bool IsBusy => _tasks.IsRunning;

    public event EventHandler? BusyChanged;

    public void ReleaseIdleProcesses(ToolId tool) => _releaseIdle(tool);

    private void OnTasksChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskCenter.IsRunning))
        {
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
