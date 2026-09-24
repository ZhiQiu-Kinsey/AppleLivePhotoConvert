using System.Globalization;
using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Updates;

/// <summary>更新相关的界面文案：失败原因与下载大小。</summary>
public static class UpdateTexts
{
    public static string FailureKey(UpdateFailureKind kind) => kind switch
    {
        UpdateFailureKind.Network => "UpdateFailureNetwork",
        UpdateFailureKind.RateLimited => "UpdateFailureRateLimited",
        UpdateFailureKind.Integrity => "UpdateFailureIntegrity",
        UpdateFailureKind.Disk => "UpdateFailureDisk",
        UpdateFailureKind.NotFound => "UpdateFailureNotFound",
        _ => "UpdateFailureUnknown"
    };

    public static string Failure(ILocalizer localizer, UpdateFailureKind kind) => localizer[FailureKey(kind)];

    public static string Failure(ILocalizer localizer, Exception ex) =>
        Failure(localizer, ex is UpdateException update ? update.Kind : UpdateFailureKind.Unknown);

    public static string Size(ILocalizer localizer, AvailableUpdate update) => localizer.Format(
        update.IsDelta ? "UpdateSizeDeltaFormat" : "UpdateSizeFullFormat",
        ByteSizeConverter.Instance.Convert(update.DownloadBytes, typeof(string), null, CultureInfo.InvariantCulture));
}
