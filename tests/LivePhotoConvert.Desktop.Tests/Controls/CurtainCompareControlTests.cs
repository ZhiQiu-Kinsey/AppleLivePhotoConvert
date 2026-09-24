using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Controls;

/// <summary>通用卷帘对比控件：属性约束、主题配色、缩放/平移/分割线交互、放大镜与放大视图的细节请求、角标不被裁剪。</summary>
[Collection(ProcessStateCollection.Name)]
public class CurtainCompareControlTests
{
    private static readonly Color BeforeColor = Color.FromRgb(220, 30, 30);
    private static readonly Color AfterColor = Color.FromRgb(30, 200, 60);

    [AvaloniaFact]
    public void Properties_HaveDefaultsAndAreCoerced()
    {
        var control = new CurtainCompareControl();

        Assert.Equal(0.5, control.CurtainPosition);
        Assert.Equal(1, control.Zoom);
        Assert.False(control.IsMagnifierEnabled);

        control.Zoom = 20;
        Assert.Equal(CompareViewport.MaxZoom, control.Zoom);
        control.Zoom = 0.2;
        Assert.Equal(CompareViewport.MinZoom, control.Zoom);
        control.CurtainPosition = 3;
        Assert.Equal(1, control.CurtainPosition);
        control.CurtainPosition = double.NaN;
        Assert.Equal(0.5, control.CurtainPosition);
    }

    [AvaloniaFact]
    public void ThemeStyle_SuppliesEveryBrushFromTokens()
    {
        using var host = Host.Create(new PixelSize(1200, 900));
        var control = host.Control;

        Assert.True(control.TryFindResource("MediaCanvasBrush", control.ActualThemeVariant, out var canvas));
        Assert.Same(canvas, control.Background);
        Assert.NotNull(control.DividerBrush);
        Assert.NotNull(control.HandleForeground);
        Assert.NotNull(control.BadgeBackground);
        Assert.NotNull(control.AfterBadgeBackground);
        Assert.NotNull(control.BadgeForeground);
        Assert.NotNull(control.MagnifierBorderBrush);
    }

    [AvaloniaFact]
    public void RequestedPixelSize_IsFitRectTimesRenderScaling()
    {
        using var host = Host.Create(new PixelSize(1200, 900));

        // 600×400 的视口里 4:3 图片适配为 533.3×400
        Assert.Equal(1, host.Control.RenderScaling);
        Assert.Equal(new PixelSize(534, 400), host.Control.RequestedPixelSize);

        host.Control.SourcePixelSize = new PixelSize(900, 1200);
        host.Pump();
        Assert.Equal(new PixelSize(300, 400), host.Control.RequestedPixelSize);
    }

    [AvaloniaFact]
    public void Wheel_ZoomsAroundPointer()
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        var pointer = new Point(420, 150);
        var before = host.Control.Viewport.ToSource(pointer);

        host.Window.MouseWheel(pointer, new Vector(0, 1));
        host.Pump();

        Assert.Equal(CurtainCompareControl.WheelZoomStep, host.Control.Zoom, 9);
        var after = host.Control.Viewport.ToSource(pointer);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        for (var i = 0; i < 20; i++)
        {
            host.Window.MouseWheel(pointer, new Vector(0, 1));
        }

        host.Pump();
        Assert.Equal(CompareViewport.MaxZoom, host.Control.Zoom);
    }

    [AvaloniaFact]
    public void Drag_AwayFromDividerWhenZoomed_Pans_AndNearDivider_MovesCurtain()
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        host.Control.Zoom = 2;
        host.Pump();
        var start = new Point(450, 200);

        host.Drag(start, start + new Vector(-60, -40));

        Assert.Equal(new Vector(-60, -40), host.Control.Viewport.Pan);
        Assert.Equal(0.5, host.Control.CurtainPosition);

        host.Drag(new Point(302, 200), new Point(150, 220));

        Assert.Equal(0.25, host.Control.CurtainPosition, 6);
        Assert.Equal(new Vector(-60, -40), host.Control.Viewport.Pan);
    }

    [AvaloniaFact]
    public void Drag_AtFitZoom_AlwaysMovesCurtain()
    {
        using var host = Host.Create(new PixelSize(1200, 800));

        host.Drag(new Point(500, 200), new Point(480, 200));

        Assert.Equal(0.8, host.Control.CurtainPosition, 6);
        Assert.Equal(default, host.Control.Viewport.Pan);
    }

    [AvaloniaFact]
    public async Task Magnifier_RequestsOneToOneRegionAtPointer_AndShowsIt()
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        var source = new FakeDetailSource(new PixelSize(1200, 800));
        host.Control.DetailSource = source;
        host.Control.IsMagnifierEnabled = true;
        var pointer = new Point(300, 200);

        host.Window.MouseMove(pointer);
        host.Pump();

        var expected = host.Control.Viewport.MagnifierRegion(pointer, new Size(128, 128), 1);
        Assert.Equal(expected, host.Control.MagnifierRegion);
        Assert.Equal(new PixelRect(536, 336, 128, 128), expected);
        await host.WaitUntilAsync(() => host.Control.MagnifierDetail is not null);
        Assert.Equal(expected, host.Control.MagnifierDetail!.Region);
        Assert.Equal((expected, expected.Size), source.Requests[0]);

        // 指针离开图片后放大镜隐藏
        host.Window.MouseMove(new Point(-5, -5));
        host.Pump();
        Assert.Null(host.Control.MagnifierRegion);
    }

    [AvaloniaFact]
    public async Task Magnifier_CoalescesRequestsWhileDecoding()
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        var source = new FakeDetailSource(new PixelSize(1200, 800)) { Gate = new TaskCompletionSource() };
        host.Control.DetailSource = source;
        host.Control.IsMagnifierEnabled = true;

        for (var x = 100; x <= 500; x += 50)
        {
            host.Window.MouseMove(new Point(x, 200));
        }

        host.Pump();
        Assert.Single(source.Requests);
        Assert.True(host.Control.IsLoadingDetail);

        source.Gate.SetResult();
        var last = host.Control.MagnifierRegion!.Value;
        await host.WaitUntilAsync(() => host.Control.MagnifierDetail?.Region == last);
        // 在途期间的中间位置被合并，只补发最后一个
        Assert.Equal(2, source.Requests.Count);
    }

    [AvaloniaFact]
    public void RightButtonHold_ShowsMagnifierUntilReleased()
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        var pointer = new Point(200, 150);

        host.Window.MouseMove(pointer);
        host.Window.MouseDown(pointer, MouseButton.Right);
        host.Pump();
        Assert.NotNull(host.Control.MagnifierRegion);
        Assert.False(host.Control.IsMagnifierEnabled);

        host.Window.MouseUp(pointer, MouseButton.Right);
        host.Pump();
        Assert.Null(host.Control.MagnifierRegion);
    }

    [AvaloniaFact]
    public async Task Zoom_RequestsVisibleRegionDetail_CappedAtScreenPixels()
    {
        using var host = Host.Create(new PixelSize(3000, 2000));
        var source = new FakeDetailSource(new PixelSize(3000, 2000));
        host.Control.DetailSource = source;

        host.Control.Zoom = 2;
        await host.WaitUntilAsync(() => host.Control.ViewDetail is not null);

        var (region, output) = Assert.Single(source.Requests);
        Assert.Equal(host.Control.Viewport.VisibleSourceRegion(), region);
        Assert.Equal(new PixelRect(750, 500, 1500, 1000), region);
        Assert.Equal(new PixelSize(600, 400), output);

        host.Control.Zoom = 1;
        host.Pump();
        Assert.Null(host.Control.ViewDetail);
    }

    [AvaloniaTheory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Badges_AreDrawnAboveCurtain_AtAnyPosition(double curtain)
    {
        using var host = Host.Create(new PixelSize(1200, 800));
        host.Control.CurtainPosition = curtain;

        using var frame = host.Capture();

        // 两个角标都完整可见：角标底色压在任一侧图片之上，不是纯图片颜色
        var left = Pixel(frame, 16, 400 - 16);
        var right = Pixel(frame, 600 - 16, 400 - 16);
        Assert.NotEqual(curtain == 0 ? AfterColor : BeforeColor, left);
        Assert.NotEqual(curtain == 0 ? AfterColor : BeforeColor, right);
        // 角标之外是对应一侧的原图
        Assert.Equal(curtain == 0 ? AfterColor : BeforeColor, Pixel(frame, 300, 100));
    }

    private static Color Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var value = Marshal.ReadInt32(buffer.Address + y * buffer.RowBytes + x * 4);
        var (first, third) = ((byte)value, (byte)(value >> 16));
        return buffer.Format == PixelFormat.Rgba8888
            ? Color.FromRgb(first, (byte)(value >> 8), third)
            : Color.FromRgb(third, (byte)(value >> 8), first);
    }

    internal static Bitmap Solid(Color color, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new int[width];
        Array.Fill(row, (int)color.ToUInt32());
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, width);
        }

        return bitmap;
    }

    private sealed class Host : IDisposable
    {
        private Host(Window window, CurtainCompareControl control)
        {
            Window = window;
            Control = control;
        }

        public Window Window { get; }

        public CurtainCompareControl Control { get; }

        /// <summary>600×400 的窗口里放一个铺满的对比控件，显示位图就是 1:1 的纯色图。</summary>
        public static Host Create(PixelSize source)
        {
            var control = new CurtainCompareControl
            {
                SourcePixelSize = source,
                Before = Solid(BeforeColor, source.Width, source.Height),
                After = Solid(AfterColor, source.Width, source.Height),
                BeforeLabel = "Before",
                AfterLabel = "After"
            };
            var window = new Window { Width = 600, Height = 400, Content = control };
            window.Show();
            var host = new Host(window, control);
            host.Pump();
            return host;
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void Drag(Point from, Point to)
        {
            Window.MouseMove(from);
            Window.MouseDown(from, MouseButton.Left);
            Window.MouseMove(from + (to - from) / 2);
            Window.MouseMove(to);
            Window.MouseUp(to, MouseButton.Left);
            Pump();
        }

        public WriteableBitmap Capture()
        {
            Pump();
            return Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("没有渲染出帧。");
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "等待控件状态超时。");
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Pump();
            }
        }

        public void Dispose()
        {
            (Control.Before as IDisposable)?.Dispose();
            (Control.After as IDisposable)?.Dispose();
            Window.Close();
        }
    }

    /// <summary>按请求区域返回纯色位图并记录请求；可设闸门模拟慢速解码。</summary>
    private sealed class FakeDetailSource(PixelSize size) : ICompareDetailSource
    {
        public PixelSize SourceSize { get; } = size;

        public List<(PixelRect Region, PixelSize Output)> Requests { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public async Task<CompareDetail?> GetDetailAsync(PixelRect region, PixelSize maxOutputSize, CancellationToken cancellationToken)
        {
            Requests.Add((region, maxOutputSize));
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return new CompareDetail(region, Solid(BeforeColor, maxOutputSize.Width, maxOutputSize.Height), Solid(AfterColor, maxOutputSize.Width, maxOutputSize.Height));
        }
    }
}
