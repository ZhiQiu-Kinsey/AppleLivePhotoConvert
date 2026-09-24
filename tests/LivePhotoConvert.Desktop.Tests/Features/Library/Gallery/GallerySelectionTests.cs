using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

public class GallerySelectionTests
{
    [Fact]
    public void Counts_AreDerivedFromCardState()
    {
        var selection = new GallerySelection(Cards.Localizer);
        var ready = Cards.ApplePair("IMG_1");
        var review = Cards.Of(Cards.ApplePairItem("IMG_2", requiresReview: true));

        selection.SetScope([ready, review], new ScanCounts(Files: 7, Items: 3, Ignored: 2));

        Assert.Equal((1, 1, 0), (selection.ReadyCount, selection.SuspiciousCount, selection.SelectedCount));
        Assert.Equal((7, 3, 2), (selection.ScannedFileCount, selection.ItemCount, selection.IgnoredFileCount));
        Assert.False(review.IsSelected, "卡片默认不选中");
        Assert.Equal([ready], selection.SelectedOrAll);

        review.IsForceAccepted = true;
        Assert.Equal((2, 0), (selection.ReadyCount, selection.SuspiciousCount));
        Assert.False(review.RequiresPairReview);
        Assert.Equal(Cards.Localizer.Format("FunnelReadyFormat", 2), selection.FunnelReadyText);
        Assert.Equal([ready, review], selection.SelectedOrAll);
    }

    [Fact]
    public void FunnelTexts_ShowFilesItemsAndIgnored()
    {
        var selection = new GallerySelection(Cards.Localizer);

        selection.SetScope([Cards.ApplePair("IMG_1")], new ScanCounts(Files: 3, Items: 1, Ignored: 1));

        Assert.Equal(Cards.Localizer.Format("FunnelTotalFormat", 3), selection.FunnelTotalText);
        Assert.Equal(Cards.Localizer.Format("FunnelItemsFormat", 1), selection.FunnelItemsText);
        Assert.Equal(Cards.Localizer.Format("FunnelIgnoredFormat", 1), selection.FunnelIgnoredText);
        Assert.True(selection.HasIgnoredFiles);

        selection.SetScope([], new ScanCounts(2, 1, 0));
        Assert.False(selection.HasIgnoredFiles);
    }

    [Fact]
    public void BulkChanges_RaiseSelectionChangedOnce()
    {
        var selection = new GallerySelection(Cards.Localizer);
        PhotoCardItemViewModel[] cards = [Cards.ApplePair("1", selected: true), Cards.ApplePair("2", selected: true), Cards.ApplePair("3", selected: true)];
        var events = 0;
        PhotoCardItemViewModel? toggled = null;
        selection.SelectionChanged += (_, _) => events++;
        selection.CardToggled += (_, card) => toggled = card;

        selection.SetScope(cards, new ScanCounts(6, 3, 0));
        Assert.Equal(1, events);

        selection.SetSelected(cards, false);
        Assert.Equal(2, events);
        Assert.Equal(0, selection.SelectedCount);
        Assert.Same(cards, selection.SelectedOrAll);

        selection.SetSelected(cards, false);
        Assert.Equal(2, events);

        cards[1].ToggleSelectCommand.Execute(null);
        Assert.Equal(3, events);
        Assert.Same(cards[1], toggled);
        Assert.Equal([cards[1]], selection.SelectedOrAll);

        selection.Batch(() =>
        {
            cards[0].IsSelected = true;
            selection.SetSelected([cards[2]], true);
        });
        Assert.Equal(4, events);
        Assert.Equal(3, selection.SelectedCount);
    }

    [Fact]
    public void CardsOutsideScope_AreIgnored()
    {
        var selection = new GallerySelection(Cards.Localizer);
        var inside = Cards.ApplePair("1", selected: true);
        var outside = Cards.MotionPhoto("2", selected: true);
        selection.SetScope([inside, outside], new ScanCounts(2, 2, 0));
        selection.SetScope([inside], new ScanCounts(2, 2, 0));
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;

        outside.IsSelected = false;

        Assert.Equal(0, events);
        Assert.Equal(1, selection.SelectedCount);
    }

    [Fact]
    public void PlainClick_SelectsOnlyThatCard_AndClickingTheSoleSelectionClearsIt()
    {
        var (selection, cards) = Scope(4);
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;
        cards[3].IsSelected = true;
        events = 0;

        selection.Click(cards[1], cards, toggle: false, range: false);

        Assert.Equal([cards[1]], Selected(cards));
        Assert.Equal(1, events);
        Assert.Same(cards[1], selection.Anchor);

        selection.Click(cards[1], cards, toggle: false, range: false);
        Assert.Empty(Selected(cards));
        Assert.Equal(2, events);
        Assert.Same(cards, selection.SelectedOrAll);
    }

    [Fact]
    public void CtrlClick_TogglesOneCard_KeepingOthers()
    {
        var (selection, cards) = Scope(4);
        selection.Click(cards[0], cards, toggle: false, range: false);
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;

        selection.Click(cards[2], cards, toggle: true, range: false);
        Assert.Equal([cards[0], cards[2]], Selected(cards));

        selection.Click(cards[0], cards, toggle: true, range: false);
        Assert.Equal([cards[2]], Selected(cards));
        Assert.Equal(2, events);
    }

    /// <summary>范围按显示顺序计算（与扫描顺序不同），Shift 替换选择，Ctrl+Shift 并入选择。</summary>
    [Fact]
    public void ShiftClick_SelectsRangeInDisplayOrder()
    {
        var (selection, cards) = Scope(6);
        PhotoCardItemViewModel[] display = [cards[5], cards[4], cards[3], cards[2], cards[1], cards[0]];
        selection.Click(display[1], display, toggle: false, range: false);
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;

        selection.Click(display[3], display, toggle: false, range: true);
        Assert.Equal([cards[2], cards[3], cards[4]], Selected(cards));
        Assert.Equal(1, events);
        Assert.Same(display[1], selection.Anchor);

        // 反方向：仍以原锚点为起点，替换上一次范围
        selection.Click(display[0], display, toggle: false, range: true);
        Assert.Equal([cards[4], cards[5]], Selected(cards));

        selection.Click(display[5], display, toggle: true, range: false);
        selection.Click(display[4], display, toggle: true, range: true);
        Assert.Equal([cards[0], cards[1], cards[4], cards[5]], Selected(cards));
    }

    [Fact]
    public void ShiftClick_WithoutAnchor_SelectsSingleCard()
    {
        var (selection, cards) = Scope(3);

        selection.Click(cards[2], cards, toggle: false, range: true);

        Assert.Equal([cards[2]], Selected(cards));
        Assert.Same(cards[2], selection.Anchor);
    }

    [Fact]
    public void Clear_DeselectsScope_WithOneEvent()
    {
        var (selection, cards) = Scope(3);
        selection.SetSelected(cards, true);
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;

        selection.Clear();
        selection.Clear();

        Assert.Empty(Selected(cards));
        Assert.Equal(1, events);
    }

    private static (GallerySelection Selection, PhotoCardItemViewModel[] Cards) Scope(int count)
    {
        var selection = new GallerySelection(Cards.Localizer);
        PhotoCardItemViewModel[] cards = [.. Enumerable.Range(0, count).Select(i => Cards.ApplePair($"IMG_{i}"))];
        selection.SetScope(cards, new ScanCounts(count * 2, count, 0));
        return (selection, cards);
    }

    private static PhotoCardItemViewModel[] Selected(PhotoCardItemViewModel[] cards) => [.. cards.Where(c => c.IsSelected)];
}
