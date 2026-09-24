using Avalonia;
using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>屏幕工作区（物理像素）及其缩放比例。</summary>
public readonly record struct ScreenArea(PixelRect WorkingArea, double Scaling);

/// <summary>
/// 记录主窗口的常规位置、大小与最大化状态，并在启动时恢复到仍然可见的屏幕区域内。
/// </summary>
public sealed class WindowPlacementTracker(SettingsStore settings)
{
    public void Attach(Window window)
    {
        Restore(window);

        window.PositionChanged += (_, _) => Capture(window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty || e.Property == Window.WindowStateProperty)
            {
                Capture(window);
            }
        };
    }

    /// <summary>
    /// 把保存的位置修正到某个屏幕的工作区内：优先与原位置重叠最多的屏幕，否则第一个屏幕（调用方应把主屏放在首位）。
    /// 尺寸不小于最小尺寸、不大于工作区。保存值无效或没有屏幕时返回 null。
    /// </summary>
    public static WindowPlacement? Fit(WindowPlacement saved, IReadOnlyList<ScreenArea> screens, double minWidth, double minHeight)
    {
        if (screens.Count == 0 || !IsUsable(saved.Width) || !IsUsable(saved.Height))
        {
            return null;
        }

        var target = screens[0];
        long bestOverlap = 0;
        foreach (var screen in screens)
        {
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            var rect = new PixelRect(saved.X, saved.Y, ToPixels(saved.Width, scaling), ToPixels(saved.Height, scaling));
            var overlap = rect.Intersect(screen.WorkingArea);
            long area = (long)overlap.Width * overlap.Height;
            if (area > bestOverlap)
            {
                bestOverlap = area;
                target = screen;
            }
        }

        var s = target.Scaling > 0 ? target.Scaling : 1;
        var work = target.WorkingArea;
        double maxWidth = work.Width / s;
        double maxHeight = work.Height / s;
        double width = Math.Min(Math.Max(saved.Width, minWidth), maxWidth);
        double height = Math.Min(Math.Max(saved.Height, minHeight), maxHeight);

        int pixelWidth = ToPixels(width, s);
        int pixelHeight = ToPixels(height, s);
        int x = Math.Clamp(saved.X, work.X, Math.Max(work.X, work.Right - pixelWidth));
        int y = Math.Clamp(saved.Y, work.Y, Math.Max(work.Y, work.Bottom - pixelHeight));

        return new WindowPlacement
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            IsMaximized = saved.IsMaximized
        };
    }

    private void Restore(Window window)
    {
        if (settings.Current.Window is not { } saved)
        {
            return;
        }

        var screens = window.Screens.All
            .OrderByDescending(s => s.IsPrimary)
            .Select(s => new ScreenArea(s.WorkingArea, s.Scaling))
            .ToList();
        if (Fit(saved, screens, window.MinWidth, window.MinHeight) is not { } fitted)
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(fitted.X, fitted.Y);
        window.Width = fitted.Width;
        window.Height = fitted.Height;
        if (fitted.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void Capture(Window window)
    {
        switch (window.WindowState)
        {
            case WindowState.Normal:
                var size = window.ClientSize;
                if (!IsUsable(size.Width) || !IsUsable(size.Height))
                {
                    return;
                }

                var position = window.Position;
                settings.Update(s => s.Window = new WindowPlacement
                {
                    X = position.X,
                    Y = position.Y,
                    Width = size.Width,
                    Height = size.Height,
                    IsMaximized = false
                });
                break;

            case WindowState.Maximized:
                // 最大化时保留之前的常规尺寸，还原后才有合理大小
                settings.Update(s =>
                {
                    if (s.Window is { } previous)
                    {
                        previous.IsMaximized = true;
                    }
                });
                break;
        }
    }

    private static bool IsUsable(double value) => double.IsFinite(value) && value > 0;

    private static int ToPixels(double value, double scaling) => (int)Math.Round(value * scaling);
}
