namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>退出前需要取消并等待结束的后台任务。</summary>
public interface IBackgroundWork
{
    bool IsBusy { get; }

    /// <summary>请求取消并等待任务结束，最多等待 <paramref name="timeout"/>。</summary>
    Task CancelAndWaitAsync(TimeSpan timeout);
}
