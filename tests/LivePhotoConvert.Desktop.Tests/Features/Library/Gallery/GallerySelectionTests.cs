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

        selection.SetScope([ready, review], totalFiles: 5);

        Assert.Equal((1, 1, 1, 3, 5), (selection.ReadyCount, selection.SuspiciousCount, selection.SelectedCount, selection.FilteredCount, selection.TotalScannedCount));
        Assert.False(review.IsSelected, "待裁决的配对默认不选中");

        review.IsForceAccepted = true;
        Assert.Equal((2, 0), (selection.ReadyCount, selection.SuspiciousCount));
        Assert.False(review.RequiresPairReview);
        Assert.Equal(Cards.Localizer.Format("FunnelReadyFormat", 2), selection.FunnelReadyText);
    }

    [Fact]
    public void BulkChanges_RaiseSelectionChangedOnce()
    {
        var selection = new GallerySelection(Cards.Localizer);
        PhotoCardItemViewModel[] cards = [Cards.ApplePair("1"), Cards.ApplePair("2"), Cards.ApplePair("3")];
        var events = 0;
        PhotoCardItemViewModel? toggled = null;
        selection.SelectionChanged += (_, _) => events++;
        selection.CardToggled += (_, card) => toggled = card;

        selection.SetScope(cards, 6);
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
        var inside = Cards.ApplePair("1");
        var outside = Cards.MotionPhoto("2");
        selection.SetScope([inside, outside], 2);
        selection.SetScope([inside], 2);
        var events = 0;
        selection.SelectionChanged += (_, _) => events++;

        outside.IsSelected = false;

        Assert.Equal(0, events);
        Assert.Equal(1, selection.SelectedCount);
        Assert.Equal(1, selection.FilteredCount);
    }
}
