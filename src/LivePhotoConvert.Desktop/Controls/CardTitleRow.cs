using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 卡片信息栏的标题行：左侧标题（文件名与大小），右侧配对状态。
/// 状态显示为文字后，留给标题的宽度不少于 <see cref="MinTitleWidth"/>（标题本身更短时不少于其自然宽度）才显示文字，
/// 否则让状态内的 <see cref="CardStatusContent"/> 显示图标；按实测宽度判断，语言与文件名长短不同也不会把文件名挤成省略号。
/// </summary>
/// <remarks>状态按无限宽度测量：图标形态总能完整显示，不会被剩余宽度截掉。</remarks>
public sealed class CardTitleRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<CardTitleRow, double>(nameof(Spacing), 6);

    public static readonly StyledProperty<double> MinTitleWidthProperty =
        AvaloniaProperty.Register<CardTitleRow, double>(nameof(MinTitleWidth), 96);

    static CardTitleRow()
    {
        AffectsMeasure<CardTitleRow>(SpacingProperty, MinTitleWidthProperty);
    }

    /// <summary>标题与状态之间的间距。</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>状态显示为文字时至少留给标题的宽度。</summary>
    public double MinTitleWidth
    {
        get => GetValue(MinTitleWidthProperty);
        set => SetValue(MinTitleWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var title, var status])
        {
            return base.MeasureOverride(availableSize);
        }

        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        title.Measure(unbounded);
        status.Measure(unbounded);
        var natural = title.DesiredSize.Width;
        var width = availableSize.Width;
        if (double.IsFinite(width) && Content(status) is { } content)
        {
            // 状态徽章的边框与内边距 + 文字宽度 = 文字形态的宽度，无论当前显示哪种形态都能算出
            var wide = status.DesiredSize.Width - content.DesiredSize.Width + content.TextWidth;
            var compact = width - wide - Spacing < Math.Min(natural, MinTitleWidth);
            if (compact != content.ShowsIcon)
            {
                foreach (var each in status.GetVisualDescendants().OfType<CardStatusContent>())
                {
                    each.ShowsIcon = compact;
                    Invalidate(each);
                }

                status.Measure(unbounded);
            }
        }

        var titleWidth = double.IsFinite(width) ? Math.Max(0, width - status.DesiredSize.Width - Spacing) : natural;
        title.Measure(new Size(titleWidth, availableSize.Height));
        return new Size(
            double.IsFinite(width) ? width : natural + Spacing + status.DesiredSize.Width,
            Math.Max(title.DesiredSize.Height, status.DesiredSize.Height));
    }

    /// <summary>当前显示的状态徽章里的内容（已锁定与待裁决只显示其一）。</summary>
    private static CardStatusContent? Content(Control status) =>
        status.GetVisualDescendants().OfType<CardStatusContent>()
            .FirstOrDefault(c => c.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, status)).All(v => v.IsVisible));

    /// <summary>形态变了，内容到本行之间的各层都要重新测量，否则下一次测量会沿用缓存的尺寸。</summary>
    private void Invalidate(Visual content)
    {
        for (var visual = content; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            (visual as Layoutable)?.InvalidateMeasure();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [var title, var status])
        {
            return base.ArrangeOverride(finalSize);
        }

        var statusWidth = status.DesiredSize.Width;
        title.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - statusWidth - Spacing), finalSize.Height));
        status.Arrange(new Rect(finalSize.Width - statusWidth, 0, statusWidth, finalSize.Height));
        return finalSize;
    }
}
