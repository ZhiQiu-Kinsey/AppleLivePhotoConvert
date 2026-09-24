using Avalonia;
using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 状态徽章的内容：子元素依次为文字与图标，显示哪一个由所在的 <see cref="CardTitleRow"/> 按宽度决定。
/// </summary>
/// <remarks>
/// 两者始终参与测量：若改 IsVisible，隐藏的一方测不出宽度，标题行就无从判断能否换回文字。
/// 未显示的一方排在零尺寸处并设为透明。
/// </remarks>
public sealed class CardStatusContent : Panel
{
    /// <summary>显示图标而不是文字。</summary>
    public bool ShowsIcon { get; internal set; }

    /// <summary>文字形态的宽度（最近一次测量）。</summary>
    internal double TextWidth => Children is [var text, _] ? text.DesiredSize.Width : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var text, var icon])
        {
            return base.MeasureOverride(availableSize);
        }

        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        text.Measure(unbounded);
        icon.Measure(unbounded);
        return (ShowsIcon ? icon : text).DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [var text, var icon])
        {
            return base.ArrangeOverride(finalSize);
        }

        var (shown, hidden) = ShowsIcon ? (icon, text) : (text, icon);
        shown.Arrange(new Rect(finalSize));
        hidden.Arrange(default);
        shown.Opacity = 1;
        hidden.Opacity = 0;
        return finalSize;
    }
}
