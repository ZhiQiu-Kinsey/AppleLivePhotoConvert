using Avalonia;
using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Features.Shell;

/// <summary>
/// 内容按自然宽度放得下就完整显示，放不下就整体隐藏：次要文字宁可不显示，也不截成半句。
/// 隐藏用透明度而不改 IsVisible：不可见的子元素测量结果为零，会让"放不放得下"的判断来回翻转。
/// </summary>
public sealed class FitOrHidePanel : Panel
{
    public static readonly DirectProperty<FitOrHidePanel, bool> IsContentHiddenProperty =
        AvaloniaProperty.RegisterDirect<FitOrHidePanel, bool>(nameof(IsContentHidden), o => o.IsContentHidden);

    public bool IsContentHidden
    {
        get;
        private set => SetAndRaise(IsContentHiddenProperty, ref field, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new Size();
        foreach (var child in Children)
        {
            child.Measure(availableSize.WithWidth(double.PositiveInfinity));
            natural = new Size(Math.Max(natural.Width, child.DesiredSize.Width), Math.Max(natural.Height, child.DesiredSize.Height));
        }

        IsContentHidden = natural.Width > availableSize.Width;
        return IsContentHidden ? natural.WithWidth(0) : natural;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(new Rect(finalSize));
        }

        Opacity = IsContentHidden ? 0 : 1;
        IsHitTestVisible = !IsContentHidden;
        return finalSize;
    }
}
