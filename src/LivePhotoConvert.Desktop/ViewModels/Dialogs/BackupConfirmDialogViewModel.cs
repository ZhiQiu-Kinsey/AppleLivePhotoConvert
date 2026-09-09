using CommunityToolkit.Mvvm.Input;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>
/// 空间瘦身就地覆盖破坏性操作二次阻断弹窗 (SEC-02: .livephoto_backup)
/// </summary>
public sealed partial class BackupConfirmDialogViewModel : ViewModelBase
{
    public Action? OnConfirmed { get; init; }
    public Action? OnCancelled { get; init; }

    [RelayCommand]
    private void Confirm()
    {
        OnConfirmed?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        OnCancelled?.Invoke();
    }
}
