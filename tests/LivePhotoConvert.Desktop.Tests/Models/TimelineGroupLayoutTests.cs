using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Tests.Models;

/// <summary>等高自适应排版：完整行铺满、末行自然宽度、不拉伸图片。</summary>
public class TimelineGroupLayoutTests
{
    private static PhotoCardItemViewModel CreateLayoutCard(string key, double aspectRatio) => new()
    {
        Key = key,
        PhotoPath = $"{key}.heic",
        AspectRatio = aspectRatio,
        DateTaken = new DateTime(2025, 1, 1)
    };

    [Fact]
    public void JustifiedRows_PortraitsDoNotBecomeOversizedCards()
    {
        var cards = new List<PhotoCardItemViewModel>
        {
            CreateLayoutCard("landscape1", 4.0 / 3.0),
            CreateLayoutCard("landscape2", 4.0 / 3.0),
            CreateLayoutCard("landscape3", 4.0 / 3.0),
            CreateLayoutCard("portrait1", 3.0 / 4.0),
            CreateLayoutCard("portrait2", 3.0 / 4.0)
        };
        var group = new TimelineGroup
        {
            Header = new TimelineHeaderItemViewModel
            {
                Key = "group",
                GroupDate = new DateTime(2025, 1, 1),
                Title = string.Empty,
                LocationSummary = string.Empty
            },
            AllCards = cards
        };

        var rows = group.BuildRows("Medium", true, 1080);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].Cards.Count);
        Assert.Equal(2, rows[1].Cards.Count);
        Assert.All(rows[1].Cards, card => Assert.Equal(250, card.PreviewHeight));
        Assert.All(rows[1].Cards, card => Assert.InRange(card.DisplayWidth, 195, 196));
    }

    [Fact]
    public void JustifiedRows_AddsMorePortraitsUntilRowApproachesTargetHeight()
    {
        var cards = Enumerable.Range(1, 8)
            .Select(index => CreateLayoutCard($"portrait{index}", 3.0 / 4.0))
            .ToList();
        var group = new TimelineGroup
        {
            Header = new TimelineHeaderItemViewModel
            {
                Key = "group",
                GroupDate = new DateTime(2025, 1, 1),
                Title = string.Empty,
                LocationSummary = string.Empty
            },
            AllCards = cards
        };

        var rows = group.BuildRows("Medium", true, 1080);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Cards.Count);
        Assert.Equal(3, rows[1].Cards.Count);
        Assert.All(rows[0].Cards, card => Assert.Equal(rows[0].RowHeight, card.PreviewHeight));
        Assert.InRange(rows[0].RowHeight, 250, 275);
        Assert.Equal(250, rows[1].RowHeight);
        double occupiedWidth = rows[0].Cards.Sum(card => card.DisplayWidth) + 8 * (rows[0].Cards.Count - 1);
        Assert.Equal(1056, occupiedWidth, precision: 6);
    }

    [Theory]
    [InlineData("Small", 180)]
    [InlineData("Medium", 250)]
    [InlineData("Large", 320)]
    public void LinedFlowRows_ScaleModesKeepAspectRatiosAndFillCompleteRows(string scaleMode, double targetHeight)
    {
        var cards = Enumerable.Range(1, 16)
            .Select(index => CreateLayoutCard($"card{index}", index % 3 == 0 ? 3.0 / 4.0 : 4.0 / 3.0))
            .ToList();
        var group = new TimelineGroup
        {
            Header = new TimelineHeaderItemViewModel
            {
                Key = "group",
                GroupDate = new DateTime(2025, 1, 1),
                Title = string.Empty,
                LocationSummary = string.Empty
            },
            AllCards = cards
        };

        var rows = group.BuildRows(scaleMode, true, 1080);

        Assert.All(rows, row =>
        {
            Assert.All(row.Cards, card =>
            {
                Assert.Equal(row.RowHeight, card.PreviewHeight);
                double imageWidth = card.DisplayWidth - 8;
                Assert.Equal(card.AspectRatio, imageWidth / card.PreviewHeight, precision: 6);
            });
        });
        Assert.All(rows.Take(rows.Count - 1), row =>
        {
            Assert.InRange(row.RowHeight, targetHeight * 0.65, targetHeight * 1.5);
            double occupiedWidth = row.Cards.Sum(card => card.DisplayWidth) + 8 * (row.Cards.Count - 1);
            Assert.Equal(1056, occupiedWidth, precision: 6);
        });
    }

    [Fact]
    public void LinedFlowRows_NarrowViewportDoesNotStretchSinglePortraitAcrossTheLine()
    {
        var cards = new List<PhotoCardItemViewModel>
        {
            CreateLayoutCard("portrait", 3.0 / 4.0),
            CreateLayoutCard("landscape", 4.0 / 3.0),
            CreateLayoutCard("next", 4.0 / 3.0)
        };
        var group = new TimelineGroup
        {
            Header = new TimelineHeaderItemViewModel
            {
                Key = "group",
                GroupDate = new DateTime(2025, 1, 1),
                Title = string.Empty,
                LocationSummary = string.Empty
            },
            AllCards = cards
        };

        var rows = group.BuildRows("Medium", true, 420);

        Assert.Equal(2, rows[0].Cards.Count);
        Assert.Same(cards[0], rows[0].Cards[0]);
        Assert.Same(cards[1], rows[0].Cards[1]);
        Assert.Equal(3.0 / 4.0, (cards[0].DisplayWidth - 8) / cards[0].PreviewHeight, precision: 6);
        Assert.Equal(4.0 / 3.0, (cards[1].DisplayWidth - 8) / cards[1].PreviewHeight, precision: 6);
        double occupiedWidth = rows[0].Cards.Sum(card => card.DisplayWidth) + 8;
        Assert.Equal(396, occupiedWidth, precision: 6);
    }

    [Fact]
    public void LinedFlowRows_WiderViewportReflowsMoreCardsIntoEachLine()
    {
        var cards = Enumerable.Range(1, 20)
            .Select(index => CreateLayoutCard($"card{index}", index % 2 == 0 ? 3.0 / 4.0 : 4.0 / 3.0))
            .ToList();
        var group = new TimelineGroup
        {
            Header = new TimelineHeaderItemViewModel
            {
                Key = "group",
                GroupDate = new DateTime(2025, 1, 1),
                Title = string.Empty,
                LocationSummary = string.Empty
            },
            AllCards = cards
        };

        var narrowRows = group.BuildRows("Medium", true, 800);
        int narrowFirstRowCount = narrowRows[0].Cards.Count;
        var wideRows = group.BuildRows("Medium", true, 1600);

        Assert.True(wideRows[0].Cards.Count > narrowFirstRowCount);
        Assert.True(wideRows.Count < narrowRows.Count);
    }
}
