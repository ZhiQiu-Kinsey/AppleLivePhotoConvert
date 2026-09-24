using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LivePhotoConvert.Desktop.Features.Shell;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        // 隧道阶段处理 Esc：弹窗内的输入框等控件不会先吞掉按键
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SyncMaximized();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            SyncMaximized();
        }
    }

    private void SyncMaximized()
    {
        if (DataContext is ShellViewModel vm)
        {
            vm.IsWindowMaximized = WindowState == WindowState.Maximized;
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel vm && vm.TryCancelActiveDialog())
        {
            e.Handled = true;
        }
    }

    private void DialogOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is ShellViewModel vm && vm.TryCancelActiveDialog())
        {
            e.Handled = true;
        }
    }
}
