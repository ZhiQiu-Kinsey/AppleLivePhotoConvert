using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;

namespace LivePhotoConvert.Desktop.Features.Library;

public partial class LibraryView : UserControl
{
    private double _lastMeasuredWidth;
    private ScrollViewer? _galleryScrollViewer;
    private double _savedScrollOffsetY;
    private GalleryThumbnailBinder? _thumbnailBinder;
    private TopLevel? _topLevel;

    public LibraryView()
    {
        InitializeComponent();
        // 单层扁平化流：监听视口宽度变化，动态计算卡片宽度
        Loaded += (_, _) =>
        {
            var listBox = this.FindControl<ListBox>("GalleryListBox");
            if (listBox is not null && DataContext is LibraryViewModel vm)
            {
                AttachThumbnails(listBox, vm);
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
                    if (DataContext is not LibraryViewModel currentVm)
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

                // 首次布局完成后预热首屏视频，不能依赖鼠标经过才触发。
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is LibraryViewModel currentVm && listBox.Bounds.Height > 0)
                    {
                        currentVm.OnViewportScrolled(0, listBox.Bounds.Height);
                    }
                });
                listBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property.Name == "Bounds" && DataContext is LibraryViewModel vm2 &&
                        Math.Abs(listBox.Bounds.Width - _lastMeasuredWidth) > 2)
                    {
                        _lastMeasuredWidth = listBox.Bounds.Width;
                        vm2.UpdateCardWidth(listBox.Bounds.Width);
                    }
                };
            }
        };

        Unloaded += (_, _) => DetachThumbnails();
    }

    /// <summary>
    /// 列表容器的准备与回收驱动卡片的缩略图引用；屏幕缩放比决定缩略图档位。
    /// 视图卸载时放开全部引用，重新加载时从已实例化的容器恢复。
    /// </summary>
    private void AttachThumbnails(ListBox listBox, LibraryViewModel vm)
    {
        DetachThumbnails();
        _thumbnailBinder = new GalleryThumbnailBinder(listBox, vm.Thumbnails);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
            vm.SetRenderScaling(_topLevel.RenderScaling);
        }
    }

    /// <summary>供界面测试检查已实例化容器持有的卡片。</summary>
    internal GalleryThumbnailBinder? ThumbnailBinder => _thumbnailBinder;

    private void DetachThumbnails()
    {
        _thumbnailBinder?.Dispose();
        _thumbnailBinder = null;
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_topLevel is not null && DataContext is LibraryViewModel vm)
        {
            vm.SetRenderScaling(_topLevel.RenderScaling);
        }
    }
}
