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
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>
/// 画廊视图：把视口宽度交给排版，把列表容器接到缩略图引用计数，并在重排前后按锚点保持滚动位置；
/// 指针停在卡片预览区时经 <see cref="GalleryHoverPlayback"/> 悬浮播放；拖入文件夹即打开为相册。
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
        GalleryListBox.AddHandler(PointerPressedEvent, (_, _) => Focus(NavigationMethod.Pointer), RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
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

        // 焦点在工具栏或卡片内的按钮上时，空格与 Ctrl+A 留给该控件
        var galleryTarget = IsGalleryKeyTarget(e.Source);
        if (galleryTarget && AppShortcuts.SelectAll.Matches(e))
        {
            vm.SelectAllVisible(true);
            e.Handled = true;
        }
        else if (AppShortcuts.ClearSelection.Matches(e) && vm.Selection.SelectedCount > 0)
        {
            vm.ClearSelection();
            e.Handled = true;
        }
        else if (galleryTarget && AppShortcuts.Preview.Matches(e) && vm.PreviewTarget is { } card)
        {
            vm.OpenQuickLookCommand.Execute(card);
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var folder = _vm is { HasActiveDialog: false, CanOpenAlbum: true } && IsShown ? AlbumDrop.Resolve(e.DataTransfer) : null;
        e.DragEffects = folder is null ? DragDropEffects.None : AcceptedEffect(e.DragEffects);
        ShowDropOverlay(e.DragEffects == DragDropEffects.None ? null : folder);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ShowDropOverlay(null);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ShowDropOverlay(null);
        var folder = _vm is { HasActiveDialog: false } && IsShown ? AlbumDrop.Resolve(e.DataTransfer) : null;
        if (folder is null || _vm is not { CanOpenAlbum: true } vm)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = AcceptedEffect(e.DragEffects);
        e.Handled = true;
        vm.OpenDroppedAlbum(folder);
    }

    /// <summary>只是打开目录，不复制也不移动文件；来源不允许链接时退而用复制，告知来源不要删除原件。</summary>
    private static DragDropEffects AcceptedEffect(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : allowed & DragDropEffects.Copy;

    private void ShowDropOverlay(string? folder)
    {
        DropOverlay.IsVisible = folder is not null;
        DropOverlayPath.Text = folder;
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
