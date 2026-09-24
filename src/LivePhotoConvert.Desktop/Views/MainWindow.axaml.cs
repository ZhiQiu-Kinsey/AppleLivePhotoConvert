using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 隧道阶段处理 Esc：弹窗内的输入框等控件不会先吞掉按键
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
            vm.RequestCloseWindow = Close;
            vm.RequestMinimizeWindow = () => WindowState = WindowState.Minimized;
            vm.RequestMaximizeWindow = () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.TryCancelActiveDialog())
        {
            e.Handled = true;
        }
    }

    private void DialogOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is MainWindowViewModel vm && vm.TryCancelActiveDialog())
        {
            e.Handled = true;
        }
    }
}
