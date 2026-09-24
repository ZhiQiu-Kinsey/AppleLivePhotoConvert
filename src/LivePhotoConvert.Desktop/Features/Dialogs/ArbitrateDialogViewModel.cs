using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

public enum ArbitrationVerdict
{
    /// <summary>未做决定就关闭。</summary>
    Dismiss,

    /// <summary>确认是同一组实况，加入强制放行名单。</summary>
    Accept,

    /// <summary>确认不是同一组实况。</summary>
    Reject
}

/// <summary>
/// 图片与视频时间差超出阈值时由用户裁决是否仍视为同一组实况。
/// </summary>
public sealed partial class ArbitrateDialogViewModel(ILocalizer localizer) : DialogViewModel<ArbitrationVerdict>
{
    public required PhotoCardItemViewModel TargetCard { get; init; }

    public string PhotoTimeText => FormatTime(SafeFileTime(TargetCard.PhotoPath));

    public string VideoTimeText => FormatTime(SafeFileTime(TargetCard.VideoPath));

    public double TimeDiffSeconds => (SafeFileTime(TargetCard.PhotoPath) - SafeFileTime(TargetCard.VideoPath)).Duration().TotalSeconds;

    public string TimeDiffText => localizer.Format("ArbitrateTimeDiffFormat", TimeDiffSeconds);

    public string VerdictConclusion => localizer.Format("ArbitrateVerdictFormat", TimeDiffSeconds);

    public override object? CancelResult => ArbitrationVerdict.Dismiss;

    [RelayCommand]
    private void ConfirmWhitelist() => Close(ArbitrationVerdict.Accept);

    [RelayCommand]
    private void RejectSplit() => Close(ArbitrationVerdict.Reject);

    private static string FormatTime(DateTime dt) => dt == DateTime.MinValue ? "—" : dt.ToString("HH:mm:ss.fff");

    private static DateTime SafeFileTime(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }
}
