using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>
/// 异常实况时差超标同屏双帧裁决弹窗
/// </summary>
public sealed partial class ArbitrateDialogViewModel(ILocalizer localizer) : ViewModelBase
{
    public required PhotoCardItemViewModel TargetCard { get; init; }

    // 读取图片/视频文件时间戳，计算时差结论。
    public string PhotoTimeText => FormatTime(SafeFileTime(TargetCard.PhotoPath));
    public string VideoTimeText => FormatTime(SafeFileTime(TargetCard.VideoPath));
    public double TimeDiffSeconds => (SafeFileTime(TargetCard.PhotoPath) - SafeFileTime(TargetCard.VideoPath)).Duration().TotalSeconds;
    public string TimeDiffText => localizer.Format("ArbitrateTimeDiffFormat", TimeDiffSeconds);
    public string VerdictConclusion => localizer.Format("ArbitrateVerdictFormat", TimeDiffSeconds);

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

    public Action<PhotoCardItemViewModel>? OnWhitelistConfirmed { get; init; }
    public Action<PhotoCardItemViewModel>? OnRejectSplit { get; init; }
    public Action? OnDismiss { get; init; }

    [RelayCommand]
    private void ConfirmWhitelist()
    {
        TargetCard.IsForceAccepted = true;
        OnWhitelistConfirmed?.Invoke(TargetCard);
    }

    [RelayCommand]
    private void RejectSplit()
    {
        OnRejectSplit?.Invoke(TargetCard);
    }

    [RelayCommand]
    private void Close()
    {
        OnDismiss?.Invoke();
    }
}
