using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>不等待的任务：异常写入日志，不会成为只在终结器里被吞掉的未观察异常。</summary>
internal static class TaskFaults
{
    public static void LogFaults(this Task task, string context) =>
        task.ContinueWith(static (t, state) => ErrorLogger.Log(t.Exception!.GetBaseException(), (string)state!), context,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
