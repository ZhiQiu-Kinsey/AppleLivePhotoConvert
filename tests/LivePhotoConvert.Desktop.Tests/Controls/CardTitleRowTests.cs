using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Controls;

/// <summary>卡片标题行：状态放得下文字且不把标题挤到下限以下时显示文字，否则显示图标；宽度回升后换回文字。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class CardTitleRowTests
{
    [AvaloniaTheory]
    [InlineData("IMG_20250510_101010_0001")]
    [InlineData("IMG_1")]
    public void StatusText_NeverSqueezesTitleBelowItsMinimum(string name)
    {
        var (row, title, status) = Build(name);
        title.Measure(Size.Infinity);
        var natural = title.DesiredSize.Width;

        for (var width = 60.0; width <= 400; width += 10)
        {
            Layout(row, width);
            var room = Math.Min(natural, row.MinTitleWidth);
            if (!status.ShowsIcon)
            {
                Assert.True(title.Bounds.Width >= room - 0.5, $"宽 {width}：显示文字时标题只剩 {title.Bounds.Width:F1}，下限 {room:F1}");
            }

            Assert.True(title.Bounds.Width + row.Spacing + status.Bounds.Width <= width + 0.5, $"宽 {width}：内容溢出");
        }
    }

    [AvaloniaFact]
    public void Narrowing_SwitchesToIcon_AndWideningSwitchesBack()
    {
        var (row, _, status) = Build("IMG_20250510_101010");
        Layout(row, 400);
        Assert.False(status.ShowsIcon);
        Assert.Equal(1, status.Children[0].Opacity);
        Assert.Equal(0, status.Children[1].Opacity);

        Layout(row, 150);
        Assert.True(status.ShowsIcon);
        Assert.Equal(0, status.Children[0].Opacity);
        Assert.Equal(1, status.Children[1].Opacity);

        Layout(row, 400);
        Assert.False(status.ShowsIcon);
    }

    private static (CardTitleRow Row, TextBlock Title, CardStatusContent Status) Build(string name)
    {
        var title = new TextBlock { Text = name, FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var status = new CardStatusContent
        {
            Children =
            {
                new TextBlock { Text = "Manual Arbitration", FontSize = 11 },
                new Border { Width = 11, Height = 11 },
            },
        };
        var row = new CardTitleRow { Children = { title, new Border { Padding = new Thickness(6, 2), BorderThickness = new Thickness(1), Child = status } } };
        return (row, title, status);
    }

    private static void Layout(Control control, double width)
    {
        control.Measure(new Size(width, 40));
        control.Arrange(new Rect(0, 0, width, 40));
    }
}
