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

    // 时间取自扫描结果，打开弹窗不访问磁盘
    public string PhotoTimeText => FormatTime(TargetCard.PhotoFile.LastWriteTimeUtc);

    public string VideoTimeText => FormatTime(TargetCard.Item.Video?.LastWriteTimeUtc);

    /// <summary>优先取配对校验用的拍摄时间差，缺失时退回修改时间差。</summary>
    public double TimeDiffSeconds => TargetCard.Item.PairTimeDelta?.TotalSeconds
        ?? (TargetCard.Item.Video is { } video ? (TargetCard.PhotoFile.LastWriteTimeUtc - video.LastWriteTimeUtc).Duration().TotalSeconds : 0);

    public string VerdictConclusion => localizer.Format("ArbitrateVerdictFormat", TimeDiffSeconds);

    public override object? CancelResult => ArbitrationVerdict.Dismiss;

    [RelayCommand]
    private void ConfirmWhitelist() => Close(ArbitrationVerdict.Accept);

    [RelayCommand]
    private void RejectSplit() => Close(ArbitrationVerdict.Reject);

    private static string FormatTime(DateTime? utc) => utc is { } time && time > DateTime.MinValue
        ? time.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
        : "—";
}
