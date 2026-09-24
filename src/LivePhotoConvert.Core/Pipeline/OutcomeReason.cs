using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 条目跳过、失败或配对校验结论的原因码。Core 只给出原因码与参数，界面文案由桌面端按语言本地化。
/// </summary>
public enum OutcomeReason
{
    /// <summary>不是动态照片（未找到内嵌视频）。</summary>
    NotMotionPhoto,

    /// <summary>瘦身：无内嵌视频，且无需转换格式。</summary>
    NothingToStrip,

    /// <summary>瘦身：带 HDR 增益图且无内嵌视频，为保留 HDR 不转码。</summary>
    GainMapPreserved,

    /// <summary>配对通过：ContentIdentifier 一致。参数：标识符。</summary>
    PairContentIdentifierMatched,

    /// <summary>配对拒绝：ContentIdentifier 不一致。参数：照片标识符、视频标识符。</summary>
    PairContentIdentifierMismatch,

    /// <summary>配对拒绝：仅照片含 ContentIdentifier。</summary>
    PairContentIdentifierPhotoOnly,

    /// <summary>配对拒绝：仅视频含 ContentIdentifier。</summary>
    PairContentIdentifierVideoOnly,

    /// <summary>配对依据：拍摄时间差在阈值内。参数：时间差秒数。</summary>
    PairCaptureTimeClose,

    /// <summary>配对拒绝：拍摄时间差超过阈值。参数：时间差秒数、阈值秒数。</summary>
    PairCaptureTimeTooFar,

    /// <summary>配对拒绝：仅照片含拍摄时间。</summary>
    PairCaptureTimePhotoOnly,

    /// <summary>配对拒绝：仅视频含拍摄时间。</summary>
    PairCaptureTimeVideoOnly,

    /// <summary>配对依据：视频时长在上限内。参数：时长秒数。</summary>
    PairDurationWithinLimit,

    /// <summary>配对拒绝：视频过长，不像实况视频。参数：时长秒数、上限秒数。</summary>
    PairVideoTooLong,

    /// <summary>配对依据：缺少可校验的元数据，按文件名匹配。</summary>
    PairNameOnly,

    /// <summary>配对依据：用户人工确认。</summary>
    PairManuallyConfirmed,

    /// <summary>源文件不存在或为空。参数：文件名。</summary>
    SourceMissingOrEmpty,

    /// <summary>视频流复制与重新编码都失败。</summary>
    VideoConversionFailed,

    /// <summary>源视频是 HDR，但 FFmpeg 缺少保真所需的 10-bit HEVC 编码器。</summary>
    HdrEncoderUnavailable,

    /// <summary>外部工具缺失。参数：工具名。</summary>
    ToolMissing,

    /// <summary>输出校验未通过，已放弃写出。</summary>
    VerificationFailed,

    /// <summary>未归类的异常；原文见 <see cref="ItemOutcome.Detail"/>。</summary>
    Unexpected
}

/// <summary>
/// 一条原因：原因码加上文案需要的参数（字符串或数值，显示精度由界面格式串决定）。按值比较。
/// </summary>
public sealed record OutcomeCause
{
    public OutcomeCause(OutcomeReason reason, params IReadOnlyList<object> arguments)
    {
        Reason = reason;
        Arguments = arguments;
    }

    public OutcomeReason Reason { get; }

    public IReadOnlyList<object> Arguments { get; }

    public bool Equals(OutcomeCause? other) =>
        other is not null && Reason == other.Reason && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Reason);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }

    public static implicit operator OutcomeCause(OutcomeReason reason) => new(reason);

    /// <summary>
    /// 把条目处理中抛出的异常归类为原因码；未知异常归为 <see cref="OutcomeReason.Unexpected"/>。
    /// </summary>
    public static OutcomeCause FromException(Exception exception) => exception switch
    {
        ToolNotFoundException missing => new(OutcomeReason.ToolMissing, missing.ToolName),
        VideoConversionException { Error: VideoConversionError.HdrEncoderUnavailable } => OutcomeReason.HdrEncoderUnavailable,
        VideoConversionException => OutcomeReason.VideoConversionFailed,
        OutcomeException known => known.Cause,
        _ => OutcomeReason.Unexpected
    };
}

/// <summary>
/// 已知原因的条目失败；<see cref="Exception.Message"/> 只用于日志与详情。
/// </summary>
public sealed class OutcomeException(OutcomeCause cause, string message) : Exception(message)
{
    public OutcomeCause Cause { get; } = cause;
}
