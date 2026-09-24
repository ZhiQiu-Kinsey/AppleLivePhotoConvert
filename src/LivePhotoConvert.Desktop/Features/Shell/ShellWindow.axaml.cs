using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Shell;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        // 隧道阶段处理：Esc 先于弹窗内的输入框关闭弹窗，全局快捷键先于列表等控件的默认按键处理
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
        if (e.Handled || DataContext is not ShellViewModel vm)
        {
            return;
        }

        if (vm.HasActiveDialog)
        {
            if (AppShortcuts.CloseDialog.Matches(e) && vm.TryCancelActiveDialog())
            {
                e.Handled = true;
            }

            return;
        }

        // 下拉菜单等浮层里的按键属于浮层
        if (e.Source is Visual source && source.FindAncestorOfType<OverlayPopupHost>(includeSelf: true) is not null)
        {
            return;
        }

        if (vm.TryExecuteShortcut(e.Key, e.KeyModifiers, FocusOwnsEnter(e.Source)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 输入框总是自己处理回车；按钮、下拉框只在用键盘移入焦点时（:focus-visible）保留回车，
    /// 鼠标点过动作磁贴后按回车仍然开始动作。
    /// </summary>
    internal static bool FocusOwnsEnter(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        foreach (var node in visual.GetSelfAndVisualAncestors())
        {
            if (node is TextBox or NumericUpDown or AutoCompleteBox)
            {
                return true;
            }
        }

        return visual is Button or ComboBox && ((StyledElement)visual).Classes.Contains(":focus-visible");
    }

    private void DialogOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is ShellViewModel vm && vm.TryCancelActiveDialog())
        {
            e.Handled = true;
        }
    }
}
