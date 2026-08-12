namespace LivePhotoConvert.Core.Models;

/// <summary>
/// 配对校验的结果
/// </summary>
public sealed record PairValidationResult
{
    /// <summary>
    /// 是否通过校验，允许合成
    /// </summary>
    public required bool IsAccepted { get; init; }

    /// <summary>
    /// 校验结论的原因说明列表
    /// </summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    /// 创建通过结果
    /// </summary>
    public static PairValidationResult Accept(IReadOnlyList<string>? reasons = null) =>
        new() { IsAccepted = true, Reasons = reasons ?? [] };

    /// <summary>
    /// 创建拒绝结果
    /// </summary>
    public static PairValidationResult Reject(IReadOnlyList<string> reasons) =>
        new() { IsAccepted = false, Reasons = reasons };
}
