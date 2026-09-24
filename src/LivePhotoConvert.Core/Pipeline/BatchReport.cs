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

    /// <summary>跳过或失败的原因，按先后排列；界面据此本地化。</summary>
    public IReadOnlyList<OutcomeCause> Causes { get; init; } = [];

    /// <summary>首要原因；成功时为 <c>null</c>。</summary>
    public OutcomeReason? Reason => Causes.Count > 0 ? Causes[0].Reason : null;

    /// <summary>技术细节（如异常原文），只用于日志与详情展开，不随界面语言变化。</summary>
    public string? Detail { get; init; }

    /// <summary>瘦身释放的字节数。</summary>
    public long BytesSaved { get; init; }

    /// <summary>输出已成功，但按所选方式处理源文件时出错；内容为技术细节（文件名与异常原文）。</summary>
    public string? CleanupError { get; init; }

    /// <summary>处理过程中值得告知用户的情况（如 HDR 未能保留的原因），由界面本地化展示。</summary>
    public IReadOnlyList<OutcomeNote> Notes { get; init; } = [];

    /// <summary>瘦身时转码的 HEIC 不比保留原格式更小，因而保留了原格式（仍剥离了视频）。</summary>
    public bool KeptOriginalFormat => Notes.Any(note => note.Kind == OutcomeNoteKind.KeptOriginalFormat);

    public static ItemOutcome Succeeded(string source, params IReadOnlyList<string> outputs) =>
        new(source, OutcomeKind.Succeeded) { Outputs = outputs };

    public static ItemOutcome Skipped(string source, params IReadOnlyList<OutcomeCause> causes) =>
        new(source, OutcomeKind.Skipped) { Causes = causes };

    public static ItemOutcome Failed(string source, OutcomeCause cause, string? detail = null) =>
        new(source, OutcomeKind.Failed) { Causes = [cause], Detail = detail };

    /// <summary>按异常类型归类原因，异常原文保留在 <see cref="Detail"/>。</summary>
    public static ItemOutcome Failed(string source, Exception exception) =>
        Failed(source, OutcomeCause.FromException(exception), exception.Message);
}

/// <summary>
/// 条目附注的类别；界面按类别本地化，<see cref="OutcomeNote.Detail"/> 只作为技术细节展示。
/// </summary>
public enum OutcomeNoteKind
{
    /// <summary>已把 Apple HDR 增益图转换为 Ultra HDR 增益图写入封面。</summary>
    UltraHdrWritten,

    /// <summary>源 HEIC 没有 Apple HDR 增益图，按 SDR 输出。</summary>
    HdrGainMapMissing,

    /// <summary>源 HEIC 只有 ISO tmap 增益图（iOS 18 起可能出现），暂不支持读取，按 SDR 输出。</summary>
    HdrToneMapNotSupported,

    /// <summary>源带增益图但缺少 heif-dec，按 SDR 输出。</summary>
    HdrDecoderUnavailable,

    /// <summary>源带增益图但缺少 HDRHeadroom / HDRGain，或余量不足以产生 HDR 效果，按 SDR 输出。</summary>
    HdrMetadataMissing,

    /// <summary>增益图解码、换算、组装或校验失败，已降级为 SDR 输出。</summary>
    HdrConversionFailed,

    /// <summary>瘦身时转码的 HEIC 不比原格式小，保留了原格式，只剥离视频。</summary>
    KeptOriginalFormat
}

/// <summary>
/// 条目附注。
/// </summary>
/// <param name="Kind">类别</param>
/// <param name="Detail">技术细节（如底层错误信息），可为 <c>null</c></param>
public sealed record OutcomeNote(OutcomeNoteKind Kind, string? Detail = null);

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
/// <param name="Preresolved">处理前就已确定结果的条目数（如配对校验未通过而跳过），已计入 <paramref name="Completed"/>；
/// 吞吐与剩余时间应只按其余条目计算</param>
public readonly record struct BatchProgress(int Completed, int Total, string CurrentItem, int Preresolved = 0);
