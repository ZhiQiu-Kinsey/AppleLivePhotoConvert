using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 画廊视图：把视口宽度交给排版，把列表容器接到缩略图引用计数，并在重排前后按锚点保持滚动位置；
/// 指针停在卡片预览区时经 <see cref="GalleryHoverPlayback"/> 悬浮播放。
/// </summary>
public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;
    private ScrollViewer? _scroll;
    private GalleryThumbnailBinder? _thumbnailBinder;
    private GalleryHoverPlayback? _hover;
    private TopLevel? _topLevel;
    private (string Key, double Delta)? _anchor;
    private List<Visual> _visibilityChain = [];
    private bool _refocusAfterDialog;

    public LibraryView()
    {
        InitializeComponent();
        // 画廊自身接收焦点，Ctrl+A / Esc / 空格才能在点击卡片后生效
        Focusable = true;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        GalleryListBox.AddHandler(PointerPressedEvent, OnGalleryPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>供界面测试检查已实例化容器持有的卡片；页面隐藏时为 null。</summary>
    internal GalleryThumbnailBinder? ThumbnailBinder => _thumbnailBinder;

    /// <summary>供界面测试检查悬浮播放；视图未挂到窗口时为 null。</summary>
    internal GalleryHoverPlayback? HoverPlayback => _hover;

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
        // 卡片自身处理按下与双击，悬浮只需旁听移动，已处理的事件也要收到
        GalleryListBox.AddHandler(PointerMovedEvent, OnGalleryPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        GalleryListBox.PointerExited += OnGalleryPointerExited;
        vm.Layout.LayoutChanging += OnLayoutChanging;
        vm.Layout.LayoutChanged += OnLayoutChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
            vm.SetRenderScaling(_topLevel.RenderScaling);
            var topLevel = _topLevel;
            _hover = new GalleryHoverPlayback(vm.Playback, new TopLevelFrameScheduler(topLevel), () => topLevel.RenderScaling);
        }

        // 页面切换只改祖先的 IsVisible，视图本身不会卸载
        _visibilityChain = [this, .. this.GetVisualAncestors()];
        foreach (var visual in _visibilityChain)
        {
            visual.PropertyChanged += OnAncestorPropertyChanged;
        }

        UpdateThumbnailBinding();
        ReportViewportWidth();
    }

    private void Detach()
    {
        _hover?.Dispose();
        _hover = null;
        ReleaseThumbnails();
        foreach (var visual in _visibilityChain)
        {
            visual.PropertyChanged -= OnAncestorPropertyChanged;
        }

        _visibilityChain = [];
        GalleryListBox.SizeChanged -= OnListSizeChanged;
        GalleryListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        GalleryListBox.RemoveHandler(PointerMovedEvent, OnGalleryPointerMoved);
        GalleryListBox.PointerExited -= OnGalleryPointerExited;
        if (_vm is not null)
        {
            _vm.Layout.LayoutChanging -= OnLayoutChanging;
            _vm.Layout.LayoutChanged -= OnLayoutChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
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
    /// 看不见的画廊也不再悬浮播放。
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
            _hover?.Stop();
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

            // 滚轮滚动时指针不动，卡片却从指针下移走，必须主动停止
            if (e.OffsetDelta.Y != 0)
            {
                _hover?.Stop();
            }

            _vm?.OnViewportScrolled(scroll.Offset.Y, scroll.Viewport.Height);
        }
    }

    /// <summary>记录视口顶部的列表项与已滚过它的距离；取实际容器位置，不依赖虚拟化面板对未实例化项的估算。</summary>
    private void OnLayoutChanging(object? sender, EventArgs e)
    {
        // 重排会改变卡片尺寸与位置，正在播放的解码尺寸随之失效
        _hover?.Stop();
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

    /// <summary>
    /// 点在卡片上时画廊取得焦点；按钮（人工裁决、组标题）自己保留焦点：按钮失去焦点即取消按下，抬起时不会触发点击。
    /// </summary>
    private void OnGalleryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            Focus(NavigationMethod.Pointer);
        }
    }

    private void OnGalleryPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_hover is null || !IsShown)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<PhotoCardControl>(includeSelf: true) is { } control && control.IsInPreview(e))
        {
            if (control.DataContext is PhotoCardItemViewModel card && _vm is { } vm && !ReferenceEquals(vm.FocusedCard, card))
            {
                vm.FocusedCard = card;
            }

            _hover.Hover(control);
        }
        else
        {
            _hover.Leave();
        }
    }

    private void OnGalleryPointerExited(object? sender, PointerEventArgs e) => _hover?.Leave();

    /// <summary>弹窗（如空格打开的预览）关闭后焦点回到画廊，Esc、空格可以继续使用。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.HasActiveDialog) || _vm is null)
        {
            return;
        }

        if (_vm.HasActiveDialog)
        {
            _refocusAfterDialog = IsKeyboardFocusWithin;
        }
        else if (_refocusAfterDialog)
        {
            _refocusAfterDialog = false;
            if (IsShown)
            {
                Focus();
            }
        }
    }

    /// <summary>
    /// 隧道阶段处理，列表不会先把空格、Ctrl+A 当作行选择。主窗口的隧道处理先于这里，弹窗打开时 Esc 已被它消费。
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { HasActiveDialog: false } vm || !IsShown)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            // 焦点在工具栏或卡片内的按钮上时，空格与 Ctrl+A 留给该控件
            case Key.A or Key.Space when !IsGalleryKeyTarget(e.Source):
                break;
            case Key.A when command:
                vm.SelectAllVisible(true);
                e.Handled = true;
                break;
            case Key.Escape when vm.Selection.SelectedCount > 0:
                vm.ClearSelection();
                e.Handled = true;
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.None && vm.PreviewTarget is { } card:
                vm.OpenQuickLookCommand.Execute(card);
                e.Handled = true;
                break;
        }
    }

    private bool IsGalleryKeyTarget(object? source) =>
        ReferenceEquals(source, this) || ReferenceEquals(source, GalleryListBox) ||
        source is Visual visual && visual is not (Button or TextBox) && GalleryListBox.IsVisualAncestorOf(visual);

    private static string? KeyOf(object item) => item switch
    {
        PhotoGridRowViewModel { Cards.Count: > 0 } row => row.Cards[0].Key,
        IGalleryDisplayItem other => other.Key,
        _ => null
    };
}
