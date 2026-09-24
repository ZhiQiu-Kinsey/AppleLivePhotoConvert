using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 永久删除原片前的确认：必须键入大写 DELETE 才能确认，返回 true 表示继续。
/// </summary>
public sealed partial class DeleteConfirmDialogViewModel(ILocalizer localizer) : DialogViewModel<bool>
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _passwordInput = string.Empty;

    public int AffectedCount { get; init; }

    public string AffectedSizeText { get; init; } = string.Empty;

    public string AffectedSummaryText => localizer.Format("DeleteAffectedFormat", AffectedCount, AffectedSizeText);

    public bool CanConfirm => SafetyGuard.ValidateDeletePassword(PasswordInput);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(true);
}
