using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace LivePhotoConvert.Desktop.Features.Library;

public partial class InspectorView : UserControl
{
    private Control? _host;

    public InspectorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 自动收起按图库页（画廊 + 检查器）的宽度判断，而不是按检查器或画廊自身：
    /// 两者的宽度都随收起状态变化，用它们判断会在阈值附近来回翻转。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host = this.GetVisualParent() as Control;
        if (_host is not null)
        {
            _host.SizeChanged += OnHostSizeChanged;
        }

        ReportWidth();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_host is not null)
        {
            _host.SizeChanged -= OnHostSizeChanged;
            _host = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportWidth();
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => ReportWidth();

    private void ReportWidth()
    {
        if (DataContext is InspectorViewModel vm && _host is { } host)
        {
            vm.UpdateAvailableWidth(host.Bounds.Width);
        }
    }
}
