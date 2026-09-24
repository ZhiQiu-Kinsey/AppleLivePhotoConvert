using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LivePhotoConvert.Desktop.ViewModels.Dialogs;

namespace LivePhotoConvert.Desktop.Views.Dialogs;

public partial class QuickLookDialog : UserControl
{
    public QuickLookDialog()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Focus();
    }

    // Esc 由主窗口统一处理，这里只负责播放与切换
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not QuickLookDialogViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                vm.TogglePlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                vm.PrevItemCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.NextItemCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
