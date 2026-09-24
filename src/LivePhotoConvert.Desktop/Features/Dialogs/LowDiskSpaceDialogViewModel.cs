using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 输出盘剩余空间不足的提醒；返回 true 表示用户选择仍然继续。
/// </summary>
public sealed partial class LowDiskSpaceDialogViewModel : DialogViewModel<bool>
{
    public string TargetDirectory { get; init; } = string.Empty;

    public string RequiredSpaceText { get; init; } = string.Empty;

    public string AvailableSpaceText { get; init; } = string.Empty;

    [RelayCommand]
    private void Continue() => Close(true);
}
