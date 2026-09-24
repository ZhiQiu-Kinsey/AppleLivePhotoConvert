using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 通用的卷帘对比控件：<see cref="Before"/> 在分割线左侧、<see cref="After"/> 在右侧，两侧共用同一缩放与平移。
/// 滚轮缩放、拖动平移或移动分割线；按住右键或开启 <see cref="IsMagnifierEnabled"/> 时在指针处显示两侧 1:1 像素细节。
/// </summary>
/// <remarks>
/// <see cref="Before"/>/<see cref="After"/> 只需屏幕分辨率（见 <see cref="RequestedPixelSize"/>）；
/// 放大后的清晰画面与放大镜的像素来自 <see cref="DetailSource"/>，控件自身不持有原图。
/// </remarks>
public sealed class CurtainCompareControl : Control
{
    /// <summary>分割线两侧这个距离内按下即拖动分割线，否则（已放大时）平移。</summary>
    public const double HandleGrabDistance = 18;

    public const double WheelZoomStep = 1.25;

    private const double HandleRadius = 17;
    private const double BadgeMargin = 12;
    private const double LoupeOffset = 24;
    private static readonly TimeSpan ViewDetailDelay = TimeSpan.FromMilliseconds(150);

    public static readonly StyledProperty<IImage?> BeforeProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IImage?>(nameof(Before));

    public static readonly StyledProperty<IImage?> AfterProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IImage?>(nameof(After));

    /// <summary>原图像素尺寸；为空时取 <see cref="Before"/> 的尺寸。1:1 与区域换算以它为准。</summary>
    public static readonly StyledProperty<PixelSize> SourcePixelSizeProperty =
        AvaloniaProperty.Register<CurtainCompareControl, PixelSize>(nameof(SourcePixelSize));

    /// <summary>分割线位置，占控件宽度的比例（0～1）。</summary>
    public static readonly StyledProperty<double> CurtainPositionProperty =
        AvaloniaProperty.Register<CurtainCompareControl, double>(nameof(CurtainPosition), 0.5,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CurtainCompareControl, double>(nameof(Zoom), 1,
            defaultBindingMode: BindingMode.TwoWay, coerce: (_, value) => CompareViewport.ClampZoom(value));

    public static readonly StyledProperty<bool> IsMagnifierEnabledProperty =
        AvaloniaProperty.Register<CurtainCompareControl, bool>(nameof(IsMagnifierEnabled), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>放大镜每一侧面板的边长（DIP）。</summary>
    public static readonly StyledProperty<double> MagnifierPanelSizeProperty =
        AvaloniaProperty.Register<CurtainCompareControl, double>(nameof(MagnifierPanelSize), 128);

    public static readonly StyledProperty<ICompareDetailSource?> DetailSourceProperty =
        AvaloniaProperty.Register<CurtainCompareControl, ICompareDetailSource?>(nameof(DetailSource));

    public static readonly StyledProperty<string?> BeforeLabelProperty =
        AvaloniaProperty.Register<CurtainCompareControl, string?>(nameof(BeforeLabel));

    public static readonly StyledProperty<string?> AfterLabelProperty =
        AvaloniaProperty.Register<CurtainCompareControl, string?>(nameof(AfterLabel));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(DividerBrush));

    public static readonly StyledProperty<IBrush?> HandleForegroundProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(HandleForeground));

    public static readonly StyledProperty<IBrush?> BadgeBackgroundProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(BadgeBackground));

    public static readonly StyledProperty<IBrush?> AfterBadgeBackgroundProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(AfterBadgeBackground));

    public static readonly StyledProperty<IBrush?> BadgeForegroundProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(BadgeForeground));

    public static readonly StyledProperty<IBrush?> MagnifierBorderBrushProperty =
        AvaloniaProperty.Register<CurtainCompareControl, IBrush?>(nameof(MagnifierBorderBrush));

    /// <summary>适配显示时位图应有的像素尺寸（控件内图片区域 × 屏幕缩放），宿主据此解码 <see cref="Before"/>/<see cref="After"/>。</summary>
    public static readonly DirectProperty<CurtainCompareControl, PixelSize> RequestedPixelSizeProperty =
        AvaloniaProperty.RegisterDirect<CurtainCompareControl, PixelSize>(nameof(RequestedPixelSize), o => o.RequestedPixelSize);

    private readonly DispatcherTimer _viewDetailTimer;
    private readonly DetailSlot _loupe;
    private readonly DetailSlot _view;
    private PixelSize _requestedPixelSize;
    private Vector _pan;
    private Point? _anchor;
    private Point? _pointer;
    private bool _magnifierHeld;
    private DragMode _drag;
    private Point _dragLast;
    private TopLevel? _topLevel;
    private double _renderScaling = 1;
    private StandardCursorType? _cursorType;

    static CurtainCompareControl()
    {
        AffectsRender<CurtainCompareControl>(
            BeforeProperty, AfterProperty, CurtainPositionProperty, IsMagnifierEnabledProperty, MagnifierPanelSizeProperty,
            BeforeLabelProperty, AfterLabelProperty, BackgroundProperty, DividerBrushProperty, HandleForegroundProperty,
            BadgeBackgroundProperty, AfterBadgeBackgroundProperty, BadgeForegroundProperty, MagnifierBorderBrushProperty);
        AffectsMeasure<CurtainCompareControl>(SourcePixelSizeProperty, BeforeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<CurtainCompareControl>(true);
    }

    public CurtainCompareControl()
    {
        _viewDetailTimer = new DispatcherTimer { Interval = ViewDetailDelay };
        _viewDetailTimer.Tick += (_, _) =>
        {
            _viewDetailTimer.Stop();
            RequestViewDetail();
        };
        _loupe = new DetailSlot(this);
        _view = new DetailSlot(this);
    }

    public IImage? Before
    {
        get => GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    public IImage? After
    {
        get => GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    public PixelSize SourcePixelSize
    {
        get => GetValue(SourcePixelSizeProperty);
        set => SetValue(SourcePixelSizeProperty, value);
    }

    public double CurtainPosition
    {
        get => GetValue(CurtainPositionProperty);
        set => SetValue(CurtainPositionProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool IsMagnifierEnabled
    {
        get => GetValue(IsMagnifierEnabledProperty);
        set => SetValue(IsMagnifierEnabledProperty, value);
    }

    public double MagnifierPanelSize
    {
        get => GetValue(MagnifierPanelSizeProperty);
        set => SetValue(MagnifierPanelSizeProperty, value);
    }

    public ICompareDetailSource? DetailSource
    {
        get => GetValue(DetailSourceProperty);
        set => SetValue(DetailSourceProperty, value);
    }

    public string? BeforeLabel
    {
        get => GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    public string? AfterLabel
    {
        get => GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    public IBrush? HandleForeground
    {
        get => GetValue(HandleForegroundProperty);
        set => SetValue(HandleForegroundProperty, value);
    }

    public IBrush? BadgeBackground
    {
        get => GetValue(BadgeBackgroundProperty);
        set => SetValue(BadgeBackgroundProperty, value);
    }

    public IBrush? AfterBadgeBackground
    {
        get => GetValue(AfterBadgeBackgroundProperty);
        set => SetValue(AfterBadgeBackgroundProperty, value);
    }

    public IBrush? BadgeForeground
    {
        get => GetValue(BadgeForegroundProperty);
        set => SetValue(BadgeForegroundProperty, value);
    }

    public IBrush? MagnifierBorderBrush
    {
        get => GetValue(MagnifierBorderBrushProperty);
        set => SetValue(MagnifierBorderBrushProperty, value);
    }

    public PixelSize RequestedPixelSize
    {
        get => _requestedPixelSize;
        private set => SetAndRaise(RequestedPixelSizeProperty, ref _requestedPixelSize, value);
    }

    /// <summary>当前几何；测试据此核对缩放与平移。</summary>
    internal CompareViewport Viewport => new(Bounds.Size, EffectiveSourceSize, Zoom, _pan);

    internal double RenderScaling => _renderScaling;

    /// <summary>放大镜当前显示的原图区域；未显示时为 <c>null</c>。</summary>
    internal PixelRect? MagnifierRegion =>
        IsMagnifierActive && _pointer is { } point && Viewport.ContainsImagePoint(point)
            ? Viewport.MagnifierRegion(point, new Size(MagnifierPanelSize, MagnifierPanelSize), _renderScaling)
            : null;

    internal CompareDetail? MagnifierDetail => _loupe.Detail;

    internal CompareDetail? ViewDetail => _view.Detail;

    /// <summary>放大镜或放大视图的细节仍在加载。</summary>
    internal bool IsLoadingDetail => _loupe.IsBusy || _view.IsBusy;

    private bool IsMagnifierActive => IsMagnifierEnabled || _magnifierHeld;

    private PixelSize EffectiveSourceSize
    {
        get
        {
            if (SourcePixelSize is { Width: > 0, Height: > 0 } size)
            {
                return size;
            }

            return Before is { } image && image.Size is { Width: > 0, Height: > 0 } s
                ? new PixelSize((int)Math.Round(s.Width), (int)Math.Round(s.Height))
                : default;
        }
    }

    /// <summary>以视口中心为锚点缩放一档。</summary>
    public void ZoomBy(double factor) => SetCurrentValue(ZoomProperty, Zoom * factor);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        var source = EffectiveSourceSize;
        // 高度随图片比例变化，由 MinHeight/MaxHeight 约束在弹窗可容纳的范围内
        var height = source.Width > 0 ? width * source.Height / source.Width : 0;
        return new Size(width, double.IsFinite(availableSize.Height) ? Math.Min(height, availableSize.Height) : height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty)
        {
            var old = new CompareViewport(Bounds.Size, EffectiveSourceSize, change.GetOldValue<double>(), _pan);
            _pan = old.ZoomAt(change.GetNewValue<double>(), _anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2)).Pan;
            OnGeometryChanged();
        }
        else if (change.Property == BoundsProperty || change.Property == SourcePixelSizeProperty || change.Property == BeforeProperty)
        {
            _pan = Viewport.WithPan(_pan).Pan;
            UpdateRequestedPixelSize();
            OnGeometryChanged();
        }
        else if (change.Property == DetailSourceProperty)
        {
            _loupe.Reset();
            _view.Reset();
            OnGeometryChanged();
        }
        else if (change.Property == IsMagnifierEnabledProperty)
        {
            UpdateMagnifier();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
            _renderScaling = _topLevel.RenderScaling;
        }

        UpdateRequestedPixelSize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        _viewDetailTimer.Stop();
        _loupe.Reset();
        _view.Reset();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        if (point.Properties.IsRightButtonPressed)
        {
            _magnifierHeld = true;
            e.Pointer.Capture(this);
            UpdateMagnifier();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var nearDivider = Math.Abs(point.Position.X - DividerX) <= HandleGrabDistance;
        _drag = Zoom > CompareViewport.MinZoom && !nearDivider ? DragMode.Pan : DragMode.Curtain;
        _dragLast = point.Position;
        e.Pointer.Capture(this);
        if (_drag == DragMode.Curtain)
        {
            MoveCurtain(point.Position.X);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        switch (_drag)
        {
            case DragMode.Curtain:
                MoveCurtain(position.X);
                break;
            case DragMode.Pan:
                _pan = Viewport.PanBy(position - _dragLast).Pan;
                _dragLast = position;
                OnGeometryChanged();
                break;
        }

        UpdateCursor(_drag == DragMode.Pan ? StandardCursorType.SizeAll
            : IsMagnifierActive ? StandardCursorType.Cross
            : Math.Abs(position.X - DividerX) <= HandleGrabDistance || Zoom <= CompareViewport.MinZoom ? StandardCursorType.SizeWestEast
            : StandardCursorType.Hand);
        UpdateMagnifier();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            _magnifierHeld = false;
            UpdateMagnifier();
        }

        if (_drag != DragMode.None || e.InitialPressMouseButton == MouseButton.Right)
        {
            _drag = DragMode.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragMode.None;
        if (_magnifierHeld)
        {
            _magnifierHeld = false;
            UpdateMagnifier();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag == DragMode.None && !_magnifierHeld)
        {
            _pointer = null;
            UpdateMagnifier();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0)
        {
            return;
        }

        _anchor = e.GetPosition(this);
        try
        {
            SetCurrentValue(ZoomProperty, Zoom * Math.Pow(WheelZoomStep, e.Delta.Y));
        }
        finally
        {
            _anchor = null;
        }

        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // 背景保证整块区域可命中，拖动不会因透明像素落空
        context.FillRectangle(Background ?? Brushes.Transparent, bounds);
        var viewport = Viewport;
        if (viewport.IsEmpty || Before is null && After is null)
        {
            return;
        }

        // 原图像素放大到 2 倍以上时按最近邻显示，看得清真实像素而不是插值后的模糊
        var detailInterpolation = viewport.DevicePixelsPerSourcePixel(_renderScaling) >= 2
            ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.HighQuality;
        var dividerX = DividerX;
        var view = _view.Detail;

        DrawSide(context, After, view?.After, view, viewport, detailInterpolation);
        using (context.PushClip(new Rect(0, 0, dividerX, bounds.Height)))
        {
            DrawSide(context, Before, view?.Before, view, viewport, detailInterpolation);
        }

        var visible = viewport.VisibleRect;
        DrawDivider(context, dividerX, visible);
        DrawBadges(context, bounds);
        DrawMagnifier(context, viewport, bounds);
    }

    private double DividerX => Bounds.Width * CurtainPosition;

    private static void DrawSide(DrawingContext context, IImage? image, Bitmap? detail, CompareDetail? view, CompareViewport viewport, BitmapInterpolationMode detailInterpolation)
    {
        if (image is not null)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(image, new Rect(image.Size), viewport.ImageRect);
            }
        }

        if (detail is not null && view is not null)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = detailInterpolation }))
            {
                context.DrawImage(detail, new Rect(detail.Size), viewport.ToControl(view.Region));
            }
        }
    }

    private void DrawDivider(DrawingContext context, double x, Rect visible)
    {
        if (DividerBrush is not { } brush || visible.Height <= 0)
        {
            return;
        }

        context.FillRectangle(brush, new Rect(x - 1, visible.Top, 2, visible.Height));
        var center = new Point(x, visible.Center.Y);
        context.DrawEllipse(brush, null, center, HandleRadius, HandleRadius);
        if (HandleForeground is { } arrows)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                foreach (var direction in new[] { -1, 1 })
                {
                    var tip = new Point(center.X + direction * 10, center.Y);
                    g.BeginFigure(tip, true);
                    g.LineTo(new Point(center.X + direction * 4, center.Y - 5));
                    g.LineTo(new Point(center.X + direction * 4, center.Y + 5));
                    g.EndFigure(true);
                }
            }

            context.DrawGeometry(arrows, null, geometry);
        }
    }

    /// <summary>角标固定在控件两下角、绘制在卷帘之上，任何分割位置都完整可见。</summary>
    private void DrawBadges(DrawingContext context, Rect bounds)
    {
        if (BeforeLabel is { Length: > 0 } before)
        {
            DrawBadge(context, before, BadgeBackground, new Point(BadgeMargin, bounds.Height - BadgeMargin), alignRight: false);
        }

        if (AfterLabel is { Length: > 0 } after)
        {
            DrawBadge(context, after, AfterBadgeBackground ?? BadgeBackground, new Point(bounds.Width - BadgeMargin, bounds.Height - BadgeMargin), alignRight: true);
        }
    }

    private Rect DrawBadge(DrawingContext context, string text, IBrush? background, Point anchor, bool alignRight)
    {
        var formatted = CreateText(text, 11);
        var size = new Size(formatted.Width + 16, formatted.Height + 8);
        var rect = new Rect(new Point(alignRight ? anchor.X - size.Width : anchor.X, anchor.Y - size.Height), size);
        context.DrawRectangle(background, null, new RoundedRect(rect, 8));
        context.DrawText(formatted, rect.Position + new Point(8, 4));
        return rect;
    }

    private FormattedText CreateText(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size, BadgeForeground ?? Brushes.White);

    private void DrawMagnifier(DrawingContext context, CompareViewport viewport, Rect bounds)
    {
        if (MagnifierRegion is not { } region || _pointer is not { } pointer)
        {
            return;
        }

        var border = MagnifierBorderBrush ?? DividerBrush;
        var pen = border is null ? null : new Pen(border, 1.5);

        // 主画面上标出放大镜取样的范围
        context.DrawRectangle(null, pen, viewport.ToControl(region));

        var panel = new Size(region.Width / _renderScaling, region.Height / _renderScaling);
        var total = new Size(panel.Width * 2, panel.Height);
        var x = pointer.X + LoupeOffset + total.Width <= bounds.Width ? pointer.X + LoupeOffset : pointer.X - LoupeOffset - total.Width;
        var y = pointer.Y - LoupeOffset - total.Height >= 0 ? pointer.Y - LoupeOffset - total.Height : pointer.Y + LoupeOffset;
        var frame = new Rect(Math.Clamp(x, 0, Math.Max(0, bounds.Width - total.Width)), Math.Clamp(y, 0, Math.Max(0, bounds.Height - total.Height)), total.Width, total.Height);
        var left = new Rect(frame.Position, panel);
        var right = new Rect(new Point(frame.X + panel.Width, frame.Y), panel);

        var detail = _loupe.Detail is { } d && d.Region == region ? d : null;
        context.FillRectangle(Background ?? Brushes.Black, frame);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            DrawLoupePanel(context, detail?.Before, Before, region, left);
            DrawLoupePanel(context, detail?.After, After, region, right);
        }

        if (pen is not null)
        {
            context.DrawRectangle(null, pen, frame);
            context.DrawLine(pen, right.TopLeft, right.BottomLeft);
        }

        if (BeforeLabel is { Length: > 0 } before)
        {
            DrawBadge(context, before, BadgeBackground, left.BottomLeft + new Point(6, -6), alignRight: false);
        }

        if (AfterLabel is { Length: > 0 } after)
        {
            DrawBadge(context, after, AfterBadgeBackground ?? BadgeBackground, right.BottomRight + new Point(-6, -6), alignRight: true);
        }
    }

    /// <summary>细节未到达前先用显示位图的对应区域占位，位置与最终画面一致。</summary>
    private void DrawLoupePanel(DrawingContext context, Bitmap? detail, IImage? fallback, PixelRect region, Rect target)
    {
        using (context.PushClip(target))
        {
            if (detail is not null)
            {
                context.DrawImage(detail, new Rect(detail.Size), target);
            }
            else if (fallback is not null && EffectiveSourceSize is { Width: > 0 } source)
            {
                var sx = fallback.Size.Width / source.Width;
                var sy = fallback.Size.Height / source.Height;
                context.DrawImage(fallback, new Rect(region.X * sx, region.Y * sy, region.Width * sx, region.Height * sy), target);
            }
        }
    }

    private void UpdateCursor(StandardCursorType type)
    {
        if (_cursorType != type)
        {
            _cursorType = type;
            Cursor = new Cursor(type);
        }
    }

    private void MoveCurtain(double x)
    {
        if (Bounds.Width > 0)
        {
            SetCurrentValue(CurtainPositionProperty, x / Bounds.Width);
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        _renderScaling = _topLevel?.RenderScaling ?? 1;
        UpdateRequestedPixelSize();
        OnGeometryChanged();
    }

    private void UpdateRequestedPixelSize() => RequestedPixelSize = Viewport.FitPixelSize(_renderScaling);

    private void OnGeometryChanged()
    {
        _viewDetailTimer.Stop();
        if (Zoom > CompareViewport.MinZoom && DetailSource is not null)
        {
            _viewDetailTimer.Start();
        }
        else
        {
            _view.Reset();
        }

        UpdateMagnifier();
        InvalidateVisual();
    }

    private void UpdateMagnifier()
    {
        if (MagnifierRegion is { } region && DetailSource is { } source)
        {
            _loupe.Request(source, region, region.Size);
        }

        InvalidateVisual();
    }

    /// <summary>放大后显示位图的分辨率不够，按可见区域回原图取细节，输出不超过屏幕物理像素。</summary>
    private void RequestViewDetail()
    {
        var viewport = Viewport;
        if (DetailSource is not { } source || Zoom <= CompareViewport.MinZoom || viewport.IsEmpty)
        {
            return;
        }

        var region = viewport.VisibleSourceRegion();
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        var visible = viewport.VisibleRect;
        var output = new PixelSize(
            Math.Clamp((int)Math.Ceiling(visible.Width * _renderScaling), 1, region.Width),
            Math.Clamp((int)Math.Ceiling(visible.Height * _renderScaling), 1, region.Height));
        _view.Request(source, region, output);
    }

    private enum DragMode
    {
        None,
        Curtain,
        Pan
    }

    /// <summary>
    /// 一路细节请求：同一时间只有一个请求在途，期间的新区域只保留最后一个，完成后再补发，避免拖动时堆积解码。
    /// </summary>
    private sealed class DetailSlot(CurtainCompareControl owner)
    {
        private CancellationTokenSource? _cts;
        private (ICompareDetailSource Source, PixelRect Region, PixelSize Output)? _pending;
        private (PixelRect Region, PixelSize Output)? _inFlight;

        public CompareDetail? Detail { get; private set; }

        public bool IsBusy => _inFlight is not null;

        public void Request(ICompareDetailSource source, PixelRect region, PixelSize output)
        {
            if (Detail is { } current && current.Region == region && _inFlight is null)
            {
                return;
            }

            if (_inFlight is { } flying)
            {
                _pending = flying.Region == region && flying.Output == output ? null : (source, region, output);
                return;
            }

            _ = RunAsync(source, region, output);
        }

        public void Reset()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pending = null;
            _inFlight = null;
            Detail?.Dispose();
            Detail = null;
        }

        private async Task RunAsync(ICompareDetailSource source, PixelRect region, PixelSize output)
        {
            var cts = _cts ??= new CancellationTokenSource();
            _inFlight = (region, output);
            CompareDetail? detail = null;
            try
            {
                detail = await source.GetDetailAsync(region, output, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 细节只是增强显示，失败时保留占位画面
                Core.Services.ErrorLogger.Log(ex, "读取对比细节");
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _cts))
            {
                detail?.Dispose();
                return;
            }

            _inFlight = null;
            if (detail is not null)
            {
                Detail?.Dispose();
                Detail = detail;
                owner.InvalidateVisual();
            }

            if (_pending is { } next)
            {
                _pending = null;
                Request(next.Source, next.Region, next.Output);
            }
        }
    }
}
