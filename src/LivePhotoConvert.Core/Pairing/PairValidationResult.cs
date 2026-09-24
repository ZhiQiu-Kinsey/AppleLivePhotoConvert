namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 配对校验结果及其依据。
/// </summary>
public sealed record PairValidationResult(bool IsAccepted, IReadOnlyList<string> Reasons)
{
    public static PairValidationResult Accept(IReadOnlyList<string>? reasons = null) => new(true, reasons ?? []);

    public static PairValidationResult Reject(IReadOnlyList<string> reasons) => new(false, reasons);

    public string Summary => string.Join("；", Reasons);
}
