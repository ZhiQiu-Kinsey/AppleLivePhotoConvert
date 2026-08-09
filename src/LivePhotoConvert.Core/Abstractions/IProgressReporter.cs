namespace LivePhotoConvert.Core.Abstractions;

/// <summary>
/// 处理进度回调，由前端决定如何呈现
/// </summary>
/// <remarks>实现必须是线程安全的，多个工作线程会并发调用。</remarks>
public interface IProgressReporter
{
    /// <summary>
    /// 报告一次进度
    /// </summary>
    /// <param name="completed">已完成数量</param>
    /// <param name="total">总数量</param>
    /// <param name="currentItem">当前处理的文件名</param>
    void Report(int completed, int total, string currentItem);
}

/// <summary>
/// 不做任何输出的进度回调
/// </summary>
public sealed class NullProgressReporter : IProgressReporter
{
    /// <summary>
    /// 单例
    /// </summary>
    public static NullProgressReporter Instance { get; } = new();

    private NullProgressReporter()
    {
    }

    /// <inheritdoc />
    public void Report(int completed, int total, string currentItem)
    {
    }
}
