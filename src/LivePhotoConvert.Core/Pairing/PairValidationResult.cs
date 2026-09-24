using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 配对校验结果及其依据（原因码与参数），由界面按 <see cref="Causes"/> 本地化。
/// </summary>
public sealed record PairValidationResult
{
    public PairValidationResult(bool isAccepted, IReadOnlyList<OutcomeCause> causes)
    {
        IsAccepted = isAccepted;
        Causes = causes;
    }

    public bool IsAccepted { get; }

    /// <summary>判定依据，按判定顺序排列。</summary>
    public IReadOnlyList<OutcomeCause> Causes { get; }

    public static PairValidationResult Accept(params IReadOnlyList<OutcomeCause> causes) => new(true, causes);

    public static PairValidationResult Reject(params IReadOnlyList<OutcomeCause> causes) => new(false, causes);

    public bool Equals(PairValidationResult? other) =>
        other is not null && IsAccepted == other.IsAccepted && Causes.SequenceEqual(other.Causes);

    public override int GetHashCode() => HashCode.Combine(IsAccepted, Causes.Count);
}
