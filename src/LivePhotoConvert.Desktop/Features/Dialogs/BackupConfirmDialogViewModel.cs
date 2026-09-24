using CommunityToolkit.Mvvm.Input;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Dialogs;

/// <summary>
/// 开启就地瘦身（直接替换原文件）前的确认；返回 true 表示允许开启。
/// </summary>
public sealed partial class BackupConfirmDialogViewModel : DialogViewModel<bool>
{
    [RelayCommand]
    private void Confirm() => Close(true);
}
