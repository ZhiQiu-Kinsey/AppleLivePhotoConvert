using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.ViewModels.Dialogs;

/// <summary>通用确认弹窗；单按钮模式用于提示类消息。确认返回 true，取消或 Esc 返回 false。</summary>
public sealed partial class ConfirmDialogViewModel : DialogViewModel<bool>
{
    public required string Title { get; init; }

    public string Message { get; init; } = string.Empty;

    public required string ConfirmText { get; init; }

    public string CancelText { get; init; } = string.Empty;

    /// <summary>危险操作使用红色确认按钮与警示图标。</summary>
    public bool IsDanger { get; init; }

    public bool IsSingleButton { get; init; }

    public bool ShowCancelButton => !IsSingleButton;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    [RelayCommand]
    private void Confirm() => Close(true);
}
