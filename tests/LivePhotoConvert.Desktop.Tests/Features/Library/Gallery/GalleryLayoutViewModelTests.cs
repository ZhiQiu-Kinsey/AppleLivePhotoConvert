using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

/// <summary>排版视图模型：分组、折叠、原地更新尺寸、行对象复用与滚动锚点。</summary>
[Collection(ProcessStateCollection.Name)]
public class GalleryLayoutViewModelTests
{
    private const double Width = 1080;

    [AvaloniaFact]
    public void Grouping_ByDayMonthYear_AndNoneHidesHeaders()
    {
        var layout = Layout(Cards.Still("1", taken: new DateTime(2024, 1, 15)), Cards.Still("2", taken: new DateTime(2024, 12, 31)),
            Cards.Still("3", taken: new DateTime(2025, 3, 1)), Cards.Still("4", taken: new DateTime(2025, 3, 1, 18, 0, 0)));

        Assert.Equal(3, Headers(layout).Count);
        Assert.Equal(["2025年3月1日", "2024年12月31日", "2024年1月15日"], Headers(layout).Select(h => h.Title));
        Assert.Equal([2, 1, 1], Headers(layout).Select(h => h.PhotoCount));

        layout.SetGroupingMode("Month");
        Assert.Equal(3, Headers(layout).Count);
        layout.SetGroupingMode("Year");
        Assert.Equal(["2025年", "2024年"], Headers(layout).Select(h => h.Title));

        layout.SetGroupingMode("None");
        Assert.Empty(Headers(layout));
        Assert.Contains(layout.Items, item => item is PhotoGridRowViewModel);
        Assert.Equal(4, layout.DisplayedCards.Count);
    }

    [AvaloniaFact]
    public void ToggleAllCollapse_CollapsesThenExpands_AndCollapsedCardsAreNotDisplayed()
    {
        var layout = Layout(Cards.Still("1", taken: new DateTime(2025, 1, 1)), Cards.Still("2", taken: new DateTime(2025, 2, 1)));

        layout.ToggleAllCollapse();
        Assert.All(Headers(layout), h => Assert.True(h.IsCollapsed));
        Assert.DoesNotContain(layout.Items, item => item is PhotoGridRowViewModel);
        Assert.Empty(layout.DisplayedCards);

        layout.ToggleAllCollapse();
        Assert.All(Headers(layout), h => Assert.False(h.IsCollapsed));
        Assert.Equal(2, layout.DisplayedCards.Count);

        // 单组折叠跨重新分组保留
        var first = Headers(layout)[0];
        layout.ToggleCollapse(first);
        layout.SetSortDirection(true);
        Assert.True(layout.HeaderFor(first.Key)!.IsCollapsed);
    }

    /// <summary>宽度只变 1px：行的组成不变，列表不重置，行对象与卡片原地更新尺寸并仍然铺满。</summary>
    [AvaloniaFact]
    public void OnePixelWidthChange_UpdatesSizesInPlace_WithoutReset()
    {
        var layout = Layout(ManyCards(40));
        var rows = layout.Items.OfType<PhotoGridRowViewModel>().ToList();
        var composition = rows.Select(r => r.Cards.ToList()).ToList();
        var heights = rows.Select(r => r.RowHeight).ToList();
        var resets = 0;
        var changes = 0;
        layout.Items.CollectionChanged += (_, e) =>
        {
            changes++;
            resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        };

        layout.ApplyViewportWidth(Width + 1);

        Assert.Equal(0, resets);
        Assert.Equal(0, changes);
        var after = layout.Items.OfType<PhotoGridRowViewModel>().ToList();
        Assert.Equal(rows, after);
        Assert.Equal(composition, after.Select(r => r.Cards.ToList()));
        Assert.NotEqual(heights, after.Select(r => r.RowHeight));
        Assert.All(after.SkipLast(1), row => Assert.InRange(Occupied(row) - GalleryMetrics.LayoutWidth(Width + 1), -0.5, 0.5));
    }

    [AvaloniaFact]
    public void LargeWidthChange_ReusesRowObjects()
    {
        var layout = Layout(ManyCards(40));
        var firstRow = layout.Items.OfType<PhotoGridRowViewModel>().First();
        var firstCount = firstRow.Cards.Count;

        layout.ApplyViewportWidth(Width * 2);

        var widened = layout.Items.OfType<PhotoGridRowViewModel>().First();
        Assert.Same(firstRow, widened);
        Assert.True(widened.Cards.Count > firstCount);
        Assert.Equal(40, layout.DisplayedCards.Count);
    }

    [AvaloniaFact]
    public void SquareCrop_MakesEveryCardSquare()
    {
        var layout = Layout(ManyCards(12));

        layout.SetCropMode("Square");

        Assert.True(layout.SquareCrop);
        Assert.All(layout.DisplayedCards, c => Assert.Equal(c.PreviewHeight, c.DisplayWidth - GalleryMetrics.CardHorizontalChrome, 6));
    }

    [AvaloniaFact]
    public void Anchor_FollowsTheCardAcrossRelayout()
    {
        var layout = Layout(ManyCards(60));
        var anchorCard = layout.DisplayedCards[37];
        var index = layout.IndexOf(anchorCard.Key);
        Assert.Contains(anchorCard, Assert.IsType<PhotoGridRowViewModel>(layout.Items[index]).Cards);

        layout.ApplyViewportWidth(Width / 2);

        var narrowIndex = layout.IndexOf(anchorCard.Key);
        Assert.Contains(anchorCard, Assert.IsType<PhotoGridRowViewModel>(layout.Items[narrowIndex]).Cards);
        Assert.True(narrowIndex > index, "窄视口下同一张卡片应落在更靠后的行");
        Assert.Equal(-1, layout.IndexOf("missing"));
    }

    [AvaloniaFact]
    public async Task ViewportWidth_IsDebounced_IntoOneRelayout()
    {
        var layout = Layout(ManyCards(20));
        var relayouts = 0;
        layout.LayoutChanged += (_, _) => relayouts++;

        for (var w = 900; w < 1000; w += 10)
        {
            layout.SetViewportWidth(w);
        }

        Assert.Equal(0, relayouts);
        await Task.Delay(GalleryLayoutViewModel.WidthDebounce * 3, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, relayouts);
        Assert.Equal(990, layout.ViewportWidth);
    }

    private static GalleryLayoutViewModel Layout(params PhotoCardItemViewModel[] cards)
    {
        var layout = new GalleryLayoutViewModel(Cards.Localizer);
        layout.ApplyViewportWidth(Width);
        layout.SetCards(cards);
        return layout;
    }

    private static PhotoCardItemViewModel[] ManyCards(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Cards.Still($"IMG_{i:D4}", i % 3 == 2 ? 3.0 / 4.0 : 4.0 / 3.0, new DateTime(2025, 1, 1).AddMinutes(i)))];

    private static List<TimelineHeaderItemViewModel> Headers(GalleryLayoutViewModel layout) => [.. layout.Items.OfType<TimelineHeaderItemViewModel>()];

    private static double Occupied(PhotoGridRowViewModel row) =>
        row.Cards.Sum(c => c.DisplayWidth) + GalleryMetrics.CardSpacing * (row.Cards.Count - 1);
}
