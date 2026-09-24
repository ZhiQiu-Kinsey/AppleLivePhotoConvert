using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 配对校验结果及其依据。界面应按 <see cref="Causes"/> 本地化；<see cref="Reasons"/> 是固定中文的诊断描述。
/// </summary>
public sealed record PairValidationResult
{
    public PairValidationResult(bool isAccepted, IReadOnlyList<OutcomeCause> causes)
    {
        IsAccepted = isAccepted;
        Causes = causes;
        Reasons = [.. causes.Select(Describe)];
    }

    public bool IsAccepted { get; }

    /// <summary>判定依据（原因码与参数），顺序与 <see cref="Reasons"/> 一一对应。</summary>
    public IReadOnlyList<OutcomeCause> Causes { get; }

    /// <summary>判定依据的中文描述，用于日志与诊断。</summary>
    public IReadOnlyList<string> Reasons { get; }

    public string Summary => string.Join("；", Reasons);

    public static PairValidationResult Accept(params IReadOnlyList<OutcomeCause> causes) => new(true, causes);

    public static PairValidationResult Reject(params IReadOnlyList<OutcomeCause> causes) => new(false, causes);

    public bool Equals(PairValidationResult? other) =>
        other is not null && IsAccepted == other.IsAccepted && Causes.SequenceEqual(other.Causes);

    public override int GetHashCode() => HashCode.Combine(IsAccepted, Causes.Count);

    private static string Describe(OutcomeCause cause)
    {
        var args = cause.Arguments;
        return cause.Reason switch
        {
            OutcomeReason.PairContentIdentifierMatched => $"ContentIdentifier 一致：{args[0]}",
            OutcomeReason.PairContentIdentifierMismatch => $"ContentIdentifier 不匹配：照片={args[0]}，视频={args[1]}",
            OutcomeReason.PairContentIdentifierPhotoOnly => "仅照片含 ContentIdentifier，不像是同一张实况照片",
            OutcomeReason.PairContentIdentifierVideoOnly => "仅视频含 ContentIdentifier，不像是同一张实况照片",
            OutcomeReason.PairCaptureTimeClose => $"拍摄时间差 {args[0]:F1} 秒",
            OutcomeReason.PairCaptureTimeTooFar => $"拍摄时间差 {args[0]:F0} 秒，超过 {args[1]:F0} 秒阈值",
            OutcomeReason.PairCaptureTimePhotoOnly => "仅照片含拍摄时间，不像是同一张实况照片",
            OutcomeReason.PairCaptureTimeVideoOnly => "仅视频含拍摄时间，不像是同一张实况照片",
            OutcomeReason.PairDurationWithinLimit => $"视频时长 {args[0]:F1} 秒",
            OutcomeReason.PairVideoTooLong => $"视频时长 {args[0]:F1} 秒，超过 {args[1]:F0} 秒，不像实况视频",
            OutcomeReason.PairNameOnly => "缺少可校验的元数据，按文件名匹配",
            OutcomeReason.PairManuallyConfirmed => "人工确认配对",
            _ => cause.Reason.ToString()
        };
    }
}
