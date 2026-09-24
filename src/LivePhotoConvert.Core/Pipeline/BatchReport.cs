namespace LivePhotoConvert.Core.Pipeline;

public enum OutcomeKind
{
    Succeeded,
    Skipped,
    Failed
}

/// <summary>
/// 单个条目的处理结果。
/// </summary>
/// <param name="Source">条目的主源文件（照片）</param>
/// <param name="Kind">结果类型</param>
public sealed record ItemOutcome(string Source, OutcomeKind Kind)
{
    /// <summary>实际写出的文件。</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>跳过或失败的原因。</summary>
    public string? Message { get; init; }

    /// <summary>瘦身释放的字节数。</summary>
    public long BytesSaved { get; init; }

    /// <summary>瘦身时转码的 HEIC 不比保留原格式更小，因而保留了原格式（仍剥离了视频）。</summary>
    public bool KeptOriginalFormat { get; init; }

    /// <summary>输出已成功，但按所选方式处理源文件时出错。</summary>
    public string? CleanupError { get; init; }

    public static ItemOutcome Succeeded(string source, params IReadOnlyList<string> outputs) =>
        new(source, OutcomeKind.Succeeded) { Outputs = outputs };

    public static ItemOutcome Skipped(string source, string reason) => new(source, OutcomeKind.Skipped) { Message = reason };

    public static ItemOutcome Failed(string source, string message) => new(source, OutcomeKind.Failed) { Message = message };
}

/// <summary>
/// 一次批处理的结果。取消时只包含已经完成的条目。
/// </summary>
public sealed record BatchReport(IReadOnlyList<ItemOutcome> Items, TimeSpan Elapsed, bool Canceled)
{
    public static BatchReport Empty { get; } = new([], TimeSpan.Zero, Canceled: false);

    public int Succeeded => Items.Count(item => item.Kind == OutcomeKind.Succeeded);

    public int Skipped => Items.Count(item => item.Kind == OutcomeKind.Skipped);

    public int Failed => Items.Count(item => item.Kind == OutcomeKind.Failed);

    public int CleanupFailures => Items.Count(item => item.CleanupError is not null);

    public long BytesSaved => Items.Sum(item => item.BytesSaved);
}

/// <summary>
/// 批处理进度。
/// </summary>
/// <param name="Completed">已完成条目数</param>
/// <param name="Total">条目总数</param>
/// <param name="CurrentItem">刚完成的条目文件名</param>
public readonly record struct BatchProgress(int Completed, int Total, string CurrentItem);
