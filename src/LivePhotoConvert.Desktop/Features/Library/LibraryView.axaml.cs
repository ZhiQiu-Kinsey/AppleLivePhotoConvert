using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 画廊视图：把视口宽度交给排版，把列表容器接到缩略图引用计数，并在重排前后按锚点保持滚动位置。
/// </summary>
public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;
    private ScrollViewer? _scroll;
    private GalleryThumbnailBinder? _thumbnailBinder;
    private TopLevel? _topLevel;
    private (string Key, double Delta)? _anchor;
    private List<Visual> _visibilityChain = [];

    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>供界面测试检查已实例化容器持有的卡片；页面隐藏时为 null。</summary>
    internal GalleryThumbnailBinder? ThumbnailBinder => _thumbnailBinder;

    private void Attach()
    {
        Detach();
        if (DataContext is not LibraryViewModel vm)
        {
            return;
        }

        _vm = vm;
        GalleryListBox.SizeChanged += OnListSizeChanged;
        GalleryListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
        vm.Layout.LayoutChanging += OnLayoutChanging;
        vm.Layout.LayoutChanged += OnLayoutChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
            vm.SetRenderScaling(_topLevel.RenderScaling);
        }

        // 页面切换只改祖先的 IsVisible，视图本身不会卸载
        _visibilityChain = [this, .. this.GetVisualAncestors()];
        foreach (var visual in _visibilityChain)
        {
            visual.PropertyChanged += OnAncestorPropertyChanged;
        }

        UpdateThumbnailBinding();
        ReportViewportWidth();

        // 首次布局完成后预热首屏视频，不能依赖鼠标经过才触发
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is not null && GalleryListBox.Bounds.Height > 0)
            {
                _vm.OnViewportScrolled(0, GalleryListBox.Bounds.Height);
            }
        });
    }

    private void Detach()
    {
        ReleaseThumbnails();
        foreach (var visual in _visibilityChain)
        {
            visual.PropertyChanged -= OnAncestorPropertyChanged;
        }

        _visibilityChain = [];
        GalleryListBox.SizeChanged -= OnListSizeChanged;
        GalleryListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        if (_vm is not null)
        {
            _vm.Layout.LayoutChanging -= OnLayoutChanging;
            _vm.Layout.LayoutChanged -= OnLayoutChanged;
            _vm = null;
        }

        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }
    }

    /// <summary>
    /// 页面切走（不可见）时放开全部卡片的钉住，位图回到预算内按 LRU 驱逐；切回时从已实例化的容器重新钉住。
    /// </summary>
    private void UpdateThumbnailBinding()
    {
        if (_vm is null)
        {
            return;
        }

        if (IsShown)
        {
            _thumbnailBinder ??= new GalleryThumbnailBinder(GalleryListBox, _vm.Thumbnails);
        }
        else
        {
            ReleaseThumbnails();
        }
    }

    private bool IsShown => _visibilityChain.Count > 0 && _visibilityChain.TrueForAll(v => v.IsVisible);

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateThumbnailBinding();
        }
    }

    private void ReleaseThumbnails()
    {
        _thumbnailBinder?.Dispose();
        _thumbnailBinder = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_topLevel is not null)
        {
            _vm?.SetRenderScaling(_topLevel.RenderScaling);
        }
    }

    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e) => ReportViewportWidth();

    private void ReportViewportWidth()
    {
        _scroll ??= GalleryListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var width = _scroll is { Viewport.Width: > 0 } scroll ? scroll.Viewport.Width : GalleryListBox.Bounds.Width;
        _vm?.Layout.SetViewportWidth(width);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer scroll)
        {
            _scroll ??= scroll;
            if (e.ViewportDelta.X != 0)
            {
                ReportViewportWidth();
            }

            _vm?.OnViewportScrolled(scroll.Offset.Y, scroll.Viewport.Height);
        }
    }

    /// <summary>记录视口顶部的列表项与已滚过它的距离；取实际容器位置，不依赖虚拟化面板对未实例化项的估算。</summary>
    private void OnLayoutChanging(object? sender, EventArgs e)
    {
        _anchor = null;
        if (_scroll is not { Offset.Y: > 0.5 } scroll || !IsShown)
        {
            return;
        }

        foreach (var container in GalleryListBox.GetRealizedContainers().OrderBy(GalleryListBox.IndexFromContainer))
        {
            if (container.TranslatePoint(default, scroll) is not { } top || top.Y + container.Bounds.Height <= 0)
            {
                continue;
            }

            if (GalleryListBox.ItemFromContainer(container) is { } item && KeyOf(item) is { } key)
            {
                _anchor = (key, -top.Y);
            }

            return;
        }
    }

    /// <summary>重排后把锚点项滚回原处；锚点已不在列表中（例如被筛掉）时回到顶部。</summary>
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (_anchor is not { } anchor || _vm is null)
        {
            return;
        }

        _anchor = null;
        var index = _vm.Layout.IndexOf(anchor.Key);
        Dispatcher.UIThread.Post(() => RestoreAnchor(index, anchor.Delta), DispatcherPriority.Loaded);
    }

    private void RestoreAnchor(int index, double delta)
    {
        if (_scroll is not { } scroll || !IsShown)
        {
            return;
        }

        if (index < 0 || index >= GalleryListBox.ItemCount)
        {
            scroll.Offset = default;
            return;
        }

        GalleryListBox.ScrollIntoView(index);
        GalleryListBox.UpdateLayout();
        if (GalleryListBox.ContainerFromIndex(index) is { } container && container.TranslatePoint(default, scroll) is { } top)
        {
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + top.Y + delta));
        }
    }

    private static string? KeyOf(object item) => item switch
    {
        PhotoGridRowViewModel { Cards.Count: > 0 } row => row.Cards[0].Key,
        IGalleryDisplayItem other => other.Key,
        _ => null
    };
}
