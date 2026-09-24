using LivePhotoConvert.Desktop.Features.Library.Thumbnails;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

public class ByteBudgetTests
{
    [Fact]
    public void Evictions_FollowLeastRecentlyUsedOrder()
    {
        var budget = new ByteBudget<string>(300);
        budget.Add("a", 100);
        budget.Add("b", 100);
        budget.Add("c", 100);
        budget.Add("a", 100);
        budget.Add("d", 100);
        budget.Add("e", 100);

        Assert.Equal(["b", "c"], budget.CollectEvictions());
        Assert.Equal(300, budget.ResidentBytes);
        Assert.False(budget.Contains("b"));
        Assert.True(budget.Contains("a"));
    }

    [Fact]
    public void PinnedEntries_AreNeverEvicted()
    {
        var budget = new ByteBudget<string>(150);
        budget.Add("old", 100);
        budget.Pin("old");
        budget.Add("new", 100);

        Assert.Equal(["new"], budget.CollectEvictions());
        Assert.True(budget.Contains("old"));
        Assert.Equal(100, budget.PinnedBytes);
    }

    [Fact]
    public void AllPinned_OverBudget_EvictsNothing()
    {
        var budget = new ByteBudget<string>(100);
        foreach (var key in new[] { "a", "b", "c" })
        {
            budget.Add(key, 100);
            budget.Pin(key);
        }

        Assert.Empty(budget.CollectEvictions());
        Assert.Equal(300, budget.ResidentBytes);
        Assert.Equal(300, budget.PinnedBytes);
    }

    [Fact]
    public void Unpin_MakesEntryEvictableAgain_AndKeepsCountedPins()
    {
        var budget = new ByteBudget<string>(100);
        budget.Add("a", 100);
        budget.Pin("a");
        budget.Pin("a");
        budget.Add("b", 50);
        budget.Pin("b");

        budget.Unpin("a");
        Assert.Empty(budget.CollectEvictions());

        budget.Unpin("a");
        Assert.Equal(50, budget.PinnedBytes);
        Assert.Equal(["a"], budget.CollectEvictions());
        Assert.Equal(50, budget.ResidentBytes);
    }

    [Fact]
    public void Add_ExistingKey_UpdatesBytesAndKeepsPin()
    {
        var budget = new ByteBudget<string>(1000);
        budget.Add("a", 100);
        budget.Pin("a");
        budget.Add("a", 400);

        Assert.Equal(400, budget.ResidentBytes);
        Assert.Equal(400, budget.PinnedBytes);

        budget.Remove("a");
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.PinnedBytes);
        Assert.Equal(0, budget.Count);
    }

    [Fact]
    public void PinOnUnknownKey_IsIgnored()
    {
        var budget = new ByteBudget<string>(10);
        budget.Pin("ghost");
        budget.Unpin("ghost");
        budget.Add("ghost", 20);

        Assert.False(budget.IsPinned("ghost"));
        Assert.Equal(["ghost"], budget.CollectEvictions());
    }
}
