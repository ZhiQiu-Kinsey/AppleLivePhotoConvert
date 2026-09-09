using CommunityToolkit.Mvvm.Input;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>
/// 磁盘剩余容量不足预检阻断弹窗 (SEC-03: ENOSPC Pre-check)
/// </summary>
public sealed partial class LowDiskSpaceDialogViewModel : ViewModelBase
{
    public string TargetDirectory { get; init; } = string.Empty;
    public string RequiredSpaceText { get; init; } = string.Empty;
    public string AvailableSpaceText { get; init; } = string.Empty;

    public Action? OnDismiss { get; init; }

    [RelayCommand]
    private void Dismiss()
    {
        OnDismiss?.Invoke();
    }
}
