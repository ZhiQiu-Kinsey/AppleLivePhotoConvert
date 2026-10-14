using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Shell;

public partial class ShellWindow : Window
{
    /// <summary>与标题栏行高一致，交通灯按这个高度落在标题栏里。</summary>
    private const double TitleBarHeight = 40;

    /// <summary>三颗交通灯加上间距后，两侧留出的宽度。左右相等，标题组的中心才是窗口中心。</summary>
    private const double TrafficLightInset = 78;

    public ShellWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            UseSystemTrafficLights();
        }

        // 隧道阶段处理：Esc 先于弹窗内的输入框关闭弹窗，全局快捷键先于列表等控件的默认按键处理
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// BorderOnly 会藏起系统交通灯。macOS 改回完整装饰，标题组在栏内水平居中，并躲开两侧按钮区。
    /// </summary>
    private void UseSystemTrafficLights()
    {
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        CaptionButtons.IsVisible = false;
        TitleBar.Padding = new Thickness(TrafficLightInset, 0, TrafficLightInset, 0);
        TitleContent.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ScheduleTrafficLights();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ScheduleTrafficLights();
    }

    private bool _trafficLightQueued;

    /// <summary>系统按钮落在较矮的标题栏里，等原生布局完成后再垂直居中。</summary>
    private void ScheduleTrafficLights()
    {
        if (!OperatingSystem.IsMacOS() || _trafficLightQueued)
        {
            return;
        }

        _trafficLightQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _trafficLightQueued = false;
            MacTrafficLights.CenterIn(this, TitleBarHeight);
        }, DispatcherPriority.Render);
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
            ScheduleTrafficLights();
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
