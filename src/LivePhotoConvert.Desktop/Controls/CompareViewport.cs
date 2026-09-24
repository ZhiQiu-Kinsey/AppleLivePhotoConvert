using Avalonia;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 对比视图的几何：图片按比例适配视口后再缩放、平移；控件坐标、原图像素与屏幕物理像素之间的换算都在这里，
/// 两侧图片共用同一个视口，任何位置都严格对齐。
/// </summary>
/// <param name="Viewport">控件尺寸（DIP）</param>
/// <param name="Source">原图像素尺寸（已按方向摆正）</param>
/// <param name="Zoom">相对适配大小的倍数</param>
/// <param name="Pan">图片中心相对视口中心的偏移（DIP）</param>
public readonly record struct CompareViewport(Size Viewport, PixelSize Source, double Zoom, Vector Pan)
{
    public const double MinZoom = 1;

    public const double MaxZoom = 8;

    public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : MinZoom;

    public bool IsEmpty => Viewport.Width <= 0 || Viewport.Height <= 0 || Source.Width <= 0 || Source.Height <= 0;

    /// <summary>缩放为 1 时图片在视口中的位置：等比适配并居中。</summary>
    public Rect FitRect
    {
        get
        {
            if (IsEmpty)
            {
                return default;
            }

            var scale = Math.Min(Viewport.Width / Source.Width, Viewport.Height / Source.Height);
            var width = Source.Width * scale;
            var height = Source.Height * scale;
            return new Rect((Viewport.Width - width) / 2, (Viewport.Height - height) / 2, width, height);
        }
    }

    /// <summary>当前缩放与平移下图片在控件中的位置。</summary>
    public Rect ImageRect
    {
        get
        {
            var fit = FitRect;
            var width = fit.Width * Zoom;
            var height = fit.Height * Zoom;
            return new Rect((Viewport.Width - width) / 2 + Pan.X, (Viewport.Height - height) / 2 + Pan.Y, width, height);
        }
    }

    /// <summary>图片与视口的交集，即可见的图片区域。</summary>
    public Rect VisibleRect => IsEmpty ? default : ImageRect.Intersect(new Rect(Viewport));

    /// <summary>屏幕物理像素与原图像素之比；大于 1 表示原图像素被放大显示。</summary>
    public double DevicePixelsPerSourcePixel(double renderScaling) =>
        IsEmpty ? 0 : ImageRect.Width * renderScaling / Source.Width;

    /// <summary>适配显示所需的位图像素尺寸：按屏幕物理像素解码，不多也不少。</summary>
    public PixelSize FitPixelSize(double renderScaling)
    {
        if (IsEmpty)
        {
            return default;
        }

        var fit = FitRect;
        return new PixelSize(
            Math.Clamp((int)Math.Ceiling(fit.Width * renderScaling), 1, Source.Width),
            Math.Clamp((int)Math.Ceiling(fit.Height * renderScaling), 1, Source.Height));
    }

    /// <summary>以控件上的一点为锚点缩放：锚点下的图片内容保持不动。</summary>
    public CompareViewport ZoomAt(double zoom, Point anchor)
    {
        zoom = ClampZoom(zoom);
        if (IsEmpty)
        {
            return this with { Zoom = zoom, Pan = default };
        }

        var current = ImageRect;
        var u = (anchor.X - current.X) / current.Width;
        var v = (anchor.Y - current.Y) / current.Height;
        var fit = FitRect;
        var width = fit.Width * zoom;
        var height = fit.Height * zoom;
        var left = anchor.X - u * width;
        var top = anchor.Y - v * height;
        var pan = new Vector(left - (Viewport.Width - width) / 2, top - (Viewport.Height - height) / 2);
        return (this with { Zoom = zoom }).WithPan(pan);
    }

    public CompareViewport PanBy(Vector delta) => WithPan(Pan + delta);

    /// <summary>平移受限：放大后图片边缘不离开视口，未超出视口的方向保持居中。</summary>
    public CompareViewport WithPan(Vector pan)
    {
        var fit = FitRect;
        var limitX = Math.Max(0, (fit.Width * Zoom - Viewport.Width) / 2);
        var limitY = Math.Max(0, (fit.Height * Zoom - Viewport.Height) / 2);
        return this with { Pan = new Vector(Math.Clamp(pan.X, -limitX, limitX), Math.Clamp(pan.Y, -limitY, limitY)) };
    }

    /// <summary>视口尺寸变化后重新约束平移。</summary>
    public CompareViewport WithViewport(Size viewport, PixelSize source) =>
        (this with { Viewport = viewport, Source = source }).WithPan(Pan);

    /// <summary>控件坐标对应的原图像素坐标（可能落在图片之外）。</summary>
    public Point ToSource(Point point)
    {
        var rect = ImageRect;
        return rect.Width <= 0 || rect.Height <= 0
            ? default
            : new Point((point.X - rect.X) / rect.Width * Source.Width, (point.Y - rect.Y) / rect.Height * Source.Height);
    }

    /// <summary>原图像素区域在控件中的位置。</summary>
    public Rect ToControl(PixelRect region)
    {
        var rect = ImageRect;
        if (IsEmpty)
        {
            return default;
        }

        var sx = rect.Width / Source.Width;
        var sy = rect.Height / Source.Height;
        return new Rect(rect.X + region.X * sx, rect.Y + region.Y * sy, region.Width * sx, region.Height * sy);
    }

    public bool ContainsImagePoint(Point point) => !IsEmpty && VisibleRect.Contains(point);

    /// <summary>
    /// 放大镜 1:1 显示的原图区域：面板每个物理像素对应一个原图像素，所以区域边长 = 面板 DIP × 屏幕缩放；
    /// 以指针对应的原图位置为中心，靠近边缘时整体内移而不是缩小。
    /// </summary>
    public PixelRect MagnifierRegion(Point point, Size panel, double renderScaling)
    {
        if (IsEmpty)
        {
            return default;
        }

        var width = Math.Clamp((int)Math.Round(panel.Width * renderScaling), 1, Source.Width);
        var height = Math.Clamp((int)Math.Round(panel.Height * renderScaling), 1, Source.Height);
        var center = ToSource(point);
        var x = Math.Clamp((int)Math.Round(center.X - width / 2.0), 0, Source.Width - width);
        var y = Math.Clamp((int)Math.Round(center.Y - height / 2.0), 0, Source.Height - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>当前可见的原图区域（向外取整到整像素）。</summary>
    public PixelRect VisibleSourceRegion()
    {
        var visible = VisibleRect;
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            return default;
        }

        var topLeft = ToSource(visible.TopLeft);
        var bottomRight = ToSource(visible.BottomRight);
        var left = Math.Clamp((int)Math.Floor(topLeft.X), 0, Source.Width);
        var top = Math.Clamp((int)Math.Floor(topLeft.Y), 0, Source.Height);
        var right = Math.Clamp((int)Math.Ceiling(bottomRight.X), left, Source.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bottomRight.Y), top, Source.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
