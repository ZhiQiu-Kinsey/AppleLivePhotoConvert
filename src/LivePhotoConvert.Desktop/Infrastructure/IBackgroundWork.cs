namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>退出前需要取消并等待结束的后台任务。</summary>
public interface IBackgroundWork
{
    bool IsBusy { get; }

    /// <summary><see cref="IsBusy"/> 改变后在界面线程触发。</summary>
    event EventHandler? BusyChanged;

    /// <summary>请求取消并等待任务结束，最多等待 <paramref name="timeout"/>。</summary>
    Task CancelAndWaitAsync(TimeSpan timeout);
}
