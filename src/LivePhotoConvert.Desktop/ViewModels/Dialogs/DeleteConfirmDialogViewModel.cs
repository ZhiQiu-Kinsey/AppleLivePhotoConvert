using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>
/// 物理删除原片高危防灾弹窗（SEC-01：强制键入大写 DELETE 文本密码解锁）
/// </summary>
public sealed partial class DeleteConfirmDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _passwordInput = string.Empty;

    public int AffectedCount { get; init; }
    public string AffectedSizeText { get; init; } = string.Empty;

    /// <summary>受影响源文件汇总。</summary>
    public string AffectedSummaryText => LocalizationService.Instance.GetFormat("DeleteAffectedFormat", AffectedCount, AffectedSizeText);

    public bool CanConfirm => SafetyGuard.ValidateDeletePassword(PasswordInput);

    public Action? OnConfirmed { get; init; }
    public Action? OnCancelled { get; init; }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
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
