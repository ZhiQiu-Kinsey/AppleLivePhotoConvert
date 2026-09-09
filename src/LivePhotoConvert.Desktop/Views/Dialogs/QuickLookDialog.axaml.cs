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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Focus();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QuickLookDialogViewModel vm)
        {
            vm.Cleanup();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not QuickLookDialogViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            vm.PrevItemCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            vm.NextItemCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is QuickLookDialogViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
