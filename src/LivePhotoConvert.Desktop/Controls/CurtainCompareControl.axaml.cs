using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.ComponentModel;

namespace LivePhotoConvert.Desktop.Controls;

public partial class CurtainCompareControl : UserControl
{
    private bool _isDragging;
    private ViewModels.StripViewModel? _subscribedVm;

    public CurtainCompareControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (DataContext is ViewModels.StripViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdateMetrics();
        }
        else
        {
            _subscribedVm = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.StripViewModel.OriginalCompareBitmap)
            or nameof(ViewModels.StripViewModel.CurtainPosition))
        {
            UpdateMetrics();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateMetrics();
    }

    private Rect GetImageRenderRect()
    {
        if (DataContext is not ViewModels.StripViewModel vm || vm.OriginalCompareBitmap == null)
        {
            return new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        }

        var bmp = vm.OriginalCompareBitmap;
        if (bmp.PixelSize.Width <= 0 || bmp.PixelSize.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        }

        double imgRatio = (double)bmp.PixelSize.Width / bmp.PixelSize.Height;
        double viewRatio = Bounds.Width / Bounds.Height;

        if (viewRatio > imgRatio)
        {
            // 容器更宽：高度撑满，左右居中留边
            double renderWidth = Bounds.Height * imgRatio;
            double left = (Bounds.Width - renderWidth) / 2.0;
            return new Rect(left, 0, renderWidth, Bounds.Height);
        }
        else
        {
            // 容器更高：宽度撑满，上下居中留边
            double renderHeight = Bounds.Width / imgRatio;
            double top = (Bounds.Height - renderHeight) / 2.0;
            return new Rect(0, top, Bounds.Width, renderHeight);
        }
    }

    private void AdjustContainerHeight()
    {
        if (DataContext is not ViewModels.StripViewModel vm)
            return;

        double controlWidth = Bounds.Width;
        if (controlWidth <= 0) return;

        var bmp = vm.OriginalCompareBitmap;
        if (bmp is { PixelSize: { Width: > 0, Height: > 0 } })
        {
            double imgRatio = (double)bmp.PixelSize.Width / bmp.PixelSize.Height;
            // 依据图片宽高比计算让图片尽可能占满的自适应高度
            // 限制在合理舒适的区间 [380, 680]，兼顾大图占满与页面滚动舒适度
            double idealHeight = controlWidth / imgRatio;
            double targetHeight = Math.Clamp(idealHeight, 380.0, 680.0);
            if (double.IsNaN(Height) || Math.Abs(Height - targetHeight) > 2.0)
            {
                Height = targetHeight;
            }
        }
        else
        {
            if (!double.IsNaN(Height) && Height != 380)
            {
                Height = 380;
            }
        }
    }

    private void UpdateMetrics()
    {
        if (DataContext is not ViewModels.StripViewModel vm || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        AdjustContainerHeight();

        var rect = GetImageRenderRect();
        vm.UpdateImageGeometry(rect, Bounds.Width, Bounds.Height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            e.Pointer.Capture(this);
            UpdateCurtainPosition(pt.Position.X);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            var pt = e.GetCurrentPoint(this);
            UpdateCurtainPosition(pt.Position.X);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateCurtainPosition(double x)
    {
        var rect = GetImageRenderRect();
        if (rect.Width <= 0) return;
        double pct = Math.Clamp(((x - rect.Left) / rect.Width) * 100.0, 0.0, 100.0);
        if (DataContext is ViewModels.StripViewModel vm)
        {
            vm.CurtainPosition = pct;
        }
    }
}
