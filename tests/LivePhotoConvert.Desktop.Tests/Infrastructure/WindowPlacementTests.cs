using Avalonia;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

public class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(new PixelRect(0, 0, 1920, 1040), 1.0);
    private static readonly ScreenArea RightHiDpi = new(new PixelRect(1920, 0, 2560, 1400), 2.0);

    private static WindowPlacement Placement(int x, int y, double width, double height, bool maximized = false) =>
        new() { X = x, Y = y, Width = width, Height = height, IsMaximized = maximized };

    [Fact]
    public void Fit_InsideWorkingArea_KeepsPlacement()
    {
        var fitted = WindowPlacementTracker.Fit(Placement(100, 80, 1280, 820, maximized: true), [Primary], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(100, fitted.X);
        Assert.Equal(80, fitted.Y);
        Assert.Equal(1280, fitted.Width);
        Assert.Equal(820, fitted.Height);
        Assert.True(fitted.IsMaximized);
    }

    [Fact]
    public void Fit_OnDisconnectedMonitor_MovesToFirstScreen()
    {
        var fitted = WindowPlacementTracker.Fit(Placement(-3000, 200, 1280, 820), [Primary], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(0, fitted.X);
        Assert.Equal(200, fitted.Y);
    }

    [Fact]
    public void Fit_PartiallyOffScreen_ShiftsBackInside()
    {
        var fitted = WindowPlacementTracker.Fit(Placement(1500, 900, 1280, 820), [Primary], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(1920 - 1280, fitted.X);
        Assert.Equal(1040 - 820, fitted.Y);
    }

    [Fact]
    public void Fit_LargerThanWorkingArea_ShrinksToWorkingArea()
    {
        var fitted = WindowPlacementTracker.Fit(Placement(0, 0, 4000, 3000), [Primary], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(1920, fitted.Width);
        Assert.Equal(1040, fitted.Height);
        Assert.Equal(0, fitted.X);
        Assert.Equal(0, fitted.Y);
    }

    [Fact]
    public void Fit_SmallerThanMinimum_GrowsToMinimum()
    {
        var fitted = WindowPlacementTracker.Fit(Placement(10, 10, 300, 200), [Primary], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(960, fitted.Width);
        Assert.Equal(640, fitted.Height);
    }

    [Fact]
    public void Fit_ChoosesScreenWithLargestOverlap_AndHonorsItsScaling()
    {
        // 窗口大部分在右侧 2 倍缩放屏上：宽 1400 逻辑单位 = 2800 像素，超出该屏 2560 像素宽，应收缩到 1280
        var fitted = WindowPlacementTracker.Fit(Placement(2000, 100, 1400, 600), [Primary, RightHiDpi], 960, 640);

        Assert.NotNull(fitted);
        Assert.Equal(1280, fitted.Width);
        Assert.Equal(640, fitted.Height);
        Assert.Equal(1920, fitted.X);
        Assert.Equal(100, fitted.Y);
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(1200, 0)]
    [InlineData(double.NaN, 800)]
    [InlineData(1200, double.PositiveInfinity)]
    public void Fit_InvalidSize_ReturnsNull(double width, double height)
    {
        Assert.Null(WindowPlacementTracker.Fit(Placement(0, 0, width, height), [Primary], 960, 640));
    }

    [Fact]
    public void Fit_NoScreens_ReturnsNull()
    {
        Assert.Null(WindowPlacementTracker.Fit(Placement(0, 0, 1280, 820), [], 960, 640));
    }
}
