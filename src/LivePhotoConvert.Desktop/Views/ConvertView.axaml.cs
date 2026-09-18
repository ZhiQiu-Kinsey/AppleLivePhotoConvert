using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.Desktop.Views;

public partial class ConvertView : UserControl
{
    private double _lastMeasuredWidth;
    private ScrollViewer? _galleryScrollViewer;
    private double _savedScrollOffsetY;

    public ConvertView()
    {
        InitializeComponent();
        // 单层扁平化流：监听视口宽度变化，动态计算卡片宽度
        Loaded += (_, _) =>
        {
            var listBox = this.FindControl<ListBox>("GalleryListBox");
            if (listBox is not null && DataContext is ConvertViewModel vm)
            {
                _lastMeasuredWidth = listBox.Bounds.Width;
                vm.UpdateCardWidth(_lastMeasuredWidth);

                vm.OnBeforeStreamRebuild = () =>
                {
                    if (_galleryScrollViewer is not null)
                    {
                        _savedScrollOffsetY = _galleryScrollViewer.Offset.Y;
                    }
                };

                vm.OnAfterStreamRebuild = () =>
                {
                    if (_galleryScrollViewer is not null && _savedScrollOffsetY > 0)
                    {
                        double targetY = _savedScrollOffsetY;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_galleryScrollViewer is not null)
                            {
                                double maxY = Math.Max(0, _galleryScrollViewer.Extent.Height - _galleryScrollViewer.Viewport.Height);
                                _galleryScrollViewer.Offset = new Avalonia.Vector(_galleryScrollViewer.Offset.X, Math.Min(targetY, maxY));
                            }
                        }, DispatcherPriority.Render);
                    }
                };

                listBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, args) =>
                {
                    if (DataContext is not ConvertViewModel currentVm)
                    {
                        return;
                    }
                    if (_galleryScrollViewer is null && args.Source is ScrollViewer sv)
                    {
                        _galleryScrollViewer = sv;
                    }
                    var scroll = args.Source as ScrollViewer ?? _galleryScrollViewer;
                    if (scroll is null) return;
                    currentVm.OnViewportScrolled(scroll.Offset.Y, scroll.Viewport.Height);
                }, RoutingStrategies.Bubble);

                // 首次布局完成后主动预热首屏，不能依赖鼠标经过才触发加载。
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is ConvertViewModel currentVm && listBox.Bounds.Height > 0)
                    {
                        currentVm.OnViewportScrolled(0, listBox.Bounds.Height);
                    }
                });
                listBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == "Bounds" && DataContext is ConvertViewModel vm2 &&
                        Math.Abs(listBox.Bounds.Width - _lastMeasuredWidth) > 2)
                    {
                        _lastMeasuredWidth = listBox.Bounds.Width;
                        vm2.UpdateCardWidth(listBox.Bounds.Width);
                    }
                };
            }
        };
    }
}
