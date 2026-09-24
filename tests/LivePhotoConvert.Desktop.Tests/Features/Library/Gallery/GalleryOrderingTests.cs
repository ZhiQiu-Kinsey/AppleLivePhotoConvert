using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Gallery;

public class GalleryOrderingTests
{
    private static readonly DateTime Base = new(2025, 5, 10, 12, 0, 0);

    /// <summary>三张照片的拍摄、创建、修改时间顺序各不相同：三种日期排序得到三种不同的顺序。</summary>
    [Fact]
    public void DateSorts_UseTheirOwnTimestamps()
    {
        PhotoCardItemViewModel[] cards =
        [
            Cards.Still("A", taken: Base, created: Utc(Base.AddDays(2)), modified: Utc(Base.AddDays(1))),
            Cards.Still("B", taken: Base.AddDays(1), created: Utc(Base), modified: Utc(Base.AddDays(2))),
            Cards.Still("C", taken: Base.AddDays(2), created: Utc(Base.AddDays(1)), modified: Utc(Base))
        ];

        string Order(GallerySort sort) => string.Concat(GalleryOrdering.Sort(cards, sort, ascending: true).Select(c => c.FileName));

        Assert.Equal("ABC", Order(GallerySort.DateTaken));
        Assert.Equal("BCA", Order(GallerySort.DateCreated));
        Assert.Equal("CAB", Order(GallerySort.DateModified));
        Assert.Equal(3, new[] { Order(GallerySort.DateTaken), Order(GallerySort.DateCreated), Order(GallerySort.DateModified) }.Distinct().Count());
        Assert.Equal("CBA", string.Concat(GalleryOrdering.Sort(cards, GallerySort.DateTaken, ascending: false).Select(c => c.FileName)));
    }

    [Fact]
    public void NameSort_IsAlphabeticalInBothDirections()
    {
        PhotoCardItemViewModel[] cards = [Cards.Still("B_File", taken: Base), Cards.Still("A_File", taken: Base.AddDays(1)), Cards.Still("C_File", taken: Base.AddDays(2))];

        Assert.Equal(["A_File", "B_File", "C_File"], GalleryOrdering.Sort(cards, GallerySort.Name, true).Select(c => c.FileName));
        Assert.Equal(["C_File", "B_File", "A_File"], GalleryOrdering.Sort(cards, GallerySort.Name, false).Select(c => c.FileName));
    }

    [Theory]
    [InlineData(GalleryGrouping.Day, 3)]
    [InlineData(GalleryGrouping.Month, 2)]
    [InlineData(GalleryGrouping.Year, 1)]
    [InlineData(GalleryGrouping.None, 1)]
    public void Grouping_SplitsByPeriodAndKeepsSortDirection(GalleryGrouping grouping, int expectedGroups)
    {
        PhotoCardItemViewModel[] cards =
        [
            Cards.Still("1", taken: new DateTime(2025, 5, 2, 10, 0, 0)),
            Cards.Still("2", taken: new DateTime(2025, 5, 2, 14, 0, 0)),
            Cards.Still("3", taken: new DateTime(2025, 5, 28, 9, 0, 0)),
            Cards.Still("4", taken: new DateTime(2025, 6, 1, 9, 0, 0))
        ];
        var sorted = GalleryOrdering.Sort(cards, GallerySort.DateTaken, ascending: false);

        var groups = GalleryOrdering.Group(sorted, grouping, GallerySort.DateTaken, ascending: false);

        Assert.Equal(expectedGroups, groups.Count);
        Assert.Equal(4, groups.Sum(g => g.Cards.Count));
        Assert.Equal(["4", "3", "2", "1"], groups.SelectMany(g => g.Cards).Select(c => c.FileName));
        Assert.Equal(groups.Select(g => g.Period).OrderDescending(), groups.Select(g => g.Period));
        Assert.Equal(groups.Count, groups.Select(g => g.Key).Distinct().Count());
    }

    [Fact]
    public void Titles_AreFormattedInTheCurrentLanguage()
    {
        var localizer = new Localizer();
        var period = new DateTime(2025, 5, 10);

        Assert.Equal("2025年5月10日", GalleryOrdering.FormatTitle(localizer, GalleryGrouping.Day, period));
        Assert.Equal("2025年5月", GalleryOrdering.FormatTitle(localizer, GalleryGrouping.Month, period));
        Assert.Equal("2025年", GalleryOrdering.FormatTitle(localizer, GalleryGrouping.Year, period));
        Assert.Equal(string.Empty, GalleryOrdering.FormatTitle(localizer, GalleryGrouping.None, period));
    }

    private static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
}
