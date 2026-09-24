using Avalonia;
using LivePhotoConvert.Desktop.Controls;

namespace LivePhotoConvert.Desktop.Tests.Controls;

public class CompareViewportTests
{
    private static readonly PixelSize Source = new(4000, 3000);

    private static CompareViewport Fit(double width = 800, double height = 400) =>
        new(new Size(width, height), Source, 1, default);

    [Fact]
    public void FitRect_KeepsAspectAndCentersInViewport()
    {
        var viewport = Fit();

        var fit = viewport.FitRect;
        Assert.Equal(133.333333, fit.X, 5);
        Assert.Equal(0, fit.Y);
        Assert.Equal(533.333333, fit.Width, 5);
        Assert.Equal(400, fit.Height);
        Assert.Equal(fit.X, viewport.ImageRect.X, 9);
        Assert.Equal(fit.Width, viewport.ImageRect.Width, 9);
        Assert.Equal(fit.Width, viewport.VisibleRect.Width, 9);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(20, 8)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(3, 3)]
    public void ClampZoom_StaysWithinOneToEight(double zoom, double expected) =>
        Assert.Equal(expected, CompareViewport.ClampZoom(zoom));

    [Theory]
    [InlineData(2, 400, 200)]
    [InlineData(4, 600, 300)]
    [InlineData(8, 300, 150)]
    public void ZoomAt_KeepsContentUnderAnchor(double zoom, double x, double y)
    {
        var anchor = new Point(x, y);
        var before = Fit();

        var after = before.ZoomAt(zoom, anchor);

        Assert.Equal(zoom, after.Zoom);
        var expected = before.ToSource(anchor);
        var actual = after.ToSource(anchor);
        Assert.InRange(actual.X, expected.X - 1e-6, expected.X + 1e-6);
        Assert.InRange(actual.Y, expected.Y - 1e-6, expected.Y + 1e-6);
    }

    [Fact]
    public void ZoomAt_ReturningToOne_CentersAgain()
    {
        var zoomed = Fit().ZoomAt(4, new Point(700, 300));

        var reset = zoomed.ZoomAt(1, new Point(10, 10));

        Assert.Equal(default, reset.Pan);
        Assert.Equal(reset.FitRect, reset.ImageRect);
    }

    [Fact]
    public void Pan_IsClampedSoImageNeverLeavesViewport()
    {
        var viewport = Fit() with { Zoom = 2 };
        // 适配宽 533.3 × 2 = 1066.7 > 800，可左右各移动 133.3；高 400 × 2 = 800，上下各 200
        var panned = viewport.PanBy(new Vector(-1000, 1000));

        Assert.Equal(-133.33333333333326, panned.Pan.X, 6);
        Assert.Equal(200, panned.Pan.Y, 6);
        Assert.Equal(new Rect(0, 0, 800, 400), panned.VisibleRect);

        Assert.Equal(default, Fit().PanBy(new Vector(50, 50)).Pan);
    }

    [Fact]
    public void WithViewport_ReclampsPanAfterResize()
    {
        var panned = (Fit() with { Zoom = 2 }).PanBy(new Vector(-500, 0));

        var wider = panned.WithViewport(new Size(1200, 400), Source);

        // 更宽的视口里图片宽 1066.7 < 1200，水平方向回到居中
        Assert.Equal(0, wider.Pan.X);
    }

    [Theory]
    [InlineData(1.0, 128)]
    [InlineData(1.5, 192)]
    [InlineData(2.0, 256)]
    public void MagnifierRegion_IsOneSourcePixelPerDevicePixel(double scaling, int expectedSide)
    {
        var viewport = Fit();
        var center = new Point(400, 200);

        var region = viewport.MagnifierRegion(center, new Size(128, 128), scaling);

        Assert.Equal(new PixelSize(expectedSide, expectedSide), region.Size);
        Assert.Equal(new PixelPoint(2000 - expectedSide / 2, 1500 - expectedSide / 2), region.Position);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(4, 1.0)]
    [InlineData(4, 2.0)]
    public void MagnifierRegion_SizeIsIndependentOfZoom_AndCentersOnPointer(double zoom, double scaling)
    {
        var viewport = Fit().ZoomAt(zoom, new Point(300, 120));
        var pointer = new Point(420, 210);

        var region = viewport.MagnifierRegion(pointer, new Size(100, 80), scaling);

        Assert.Equal(new PixelSize((int)(100 * scaling), (int)(80 * scaling)), region.Size);
        var source = viewport.ToSource(pointer);
        Assert.InRange(region.X + region.Width / 2.0, source.X - 1, source.X + 1);
        Assert.InRange(region.Y + region.Height / 2.0, source.Y - 1, source.Y + 1);
    }

    [Fact]
    public void MagnifierRegion_NearEdge_ShiftsInsideInsteadOfShrinking()
    {
        var viewport = Fit();

        var topLeft = viewport.MagnifierRegion(viewport.FitRect.TopLeft, new Size(128, 128), 2);
        var bottomRight = viewport.MagnifierRegion(viewport.FitRect.BottomRight, new Size(128, 128), 2);

        Assert.Equal(new PixelRect(0, 0, 256, 256), topLeft);
        Assert.Equal(new PixelRect(4000 - 256, 3000 - 256, 256, 256), bottomRight);
    }

    [Fact]
    public void MagnifierRegion_ClampsToSmallSources()
    {
        var tiny = new CompareViewport(new Size(800, 400), new PixelSize(64, 48), 1, default);

        Assert.Equal(new PixelRect(0, 0, 64, 48), tiny.MagnifierRegion(new Point(400, 200), new Size(128, 128), 2));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void FitPixelSize_FollowsRenderScalingAndNeverExceedsSource(double scaling)
    {
        var viewport = Fit();

        var size = viewport.FitPixelSize(scaling);

        Assert.Equal(new PixelSize((int)Math.Ceiling(533.3333333333333 * scaling), (int)Math.Ceiling(400 * scaling)), size);
        Assert.Equal(new PixelSize(64, 48), new CompareViewport(new Size(800, 400), new PixelSize(64, 48), 1, default).FitPixelSize(2));
    }

    [Fact]
    public void DevicePixelsPerSourcePixel_ReachesOneAtNativeZoom()
    {
        var viewport = Fit();
        var native = Source.Width / viewport.FitRect.Width / 2;

        Assert.Equal(533.3333333333333 * 2 / 4000, viewport.DevicePixelsPerSourcePixel(2), 9);
        Assert.Equal(1, viewport.ZoomAt(native, new Point(400, 200)).DevicePixelsPerSourcePixel(2), 9);
    }

    [Fact]
    public void VisibleSourceRegion_CoversTheVisiblePartOnly()
    {
        var viewport = Fit(400, 300).ZoomAt(2, new Point(200, 150));

        Assert.Equal(new PixelRect(1000, 750, 2000, 1500), viewport.VisibleSourceRegion());
        Assert.Equal(new PixelRect(0, 0, 4000, 3000), Fit().VisibleSourceRegion());
    }

    [Fact]
    public void ToControl_IsInverseOfToSource()
    {
        var viewport = Fit().ZoomAt(3, new Point(500, 150)).PanBy(new Vector(30, -20));
        var region = new PixelRect(1234, 987, 200, 100);

        var rect = viewport.ToControl(region);

        var topLeft = viewport.ToSource(rect.TopLeft);
        var bottomRight = viewport.ToSource(rect.BottomRight);
        Assert.Equal(1234, topLeft.X, 6);
        Assert.Equal(987, topLeft.Y, 6);
        Assert.Equal(1434, bottomRight.X, 6);
        Assert.Equal(1087, bottomRight.Y, 6);
    }

    [Fact]
    public void EmptyViewport_ProducesNoGeometry()
    {
        var empty = new CompareViewport(default, Source, 1, default);

        Assert.True(empty.IsEmpty);
        Assert.Equal(default, empty.FitPixelSize(1));
        Assert.Equal(default, empty.MagnifierRegion(new Point(1, 1), new Size(128, 128), 1));
        Assert.False(empty.ContainsImagePoint(new Point(0, 0)));
    }
}
