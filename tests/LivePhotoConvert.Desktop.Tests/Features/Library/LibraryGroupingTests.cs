using System.Reflection;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>图库按日/月/年分组、排序与整体折叠。分组结果是私有状态，经反射读取。</summary>
public class LibraryGroupingTests : IDisposable
{
    private readonly DesktopTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {fieldName} not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static List<TimelineGroup> GetPrivateGroups(LibraryViewModel vm)
    {
        var field = vm.GetType().GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_groups field not found");
        return (List<TimelineGroup>)field.GetValue(vm)!;
    }

    private LibraryViewModel CreateCleanViewModel() => _host.Get<LibraryViewModel>();

    [Fact]
    public void GroupingMode_Date_GroupsByDay()
    {
        var vm = CreateCleanViewModel();

        var card1 = new PhotoCardItemViewModel
        {
            Key = "card1",
            PhotoPath = "card1.heic",
            FileName = "IMG_0001",
            DateTaken = new DateTime(2025, 5, 10, 10, 0, 0),
            FormattedDate = "2025年5月10日",
            FormattedTime = "10:00:00"
        };
        var card2 = new PhotoCardItemViewModel
        {
            Key = "card2",
            PhotoPath = "card2.heic",
            FileName = "IMG_0002",
            DateTaken = new DateTime(2025, 5, 10, 11, 0, 0),
            FormattedDate = "2025年5月10日",
            FormattedTime = "11:00:00"
        };
        var card3 = new PhotoCardItemViewModel
        {
            Key = "card3",
            PhotoPath = "card3.heic",
            FileName = "IMG_0003",
            DateTaken = new DateTime(2025, 5, 11, 9, 0, 0),
            FormattedDate = "2025年5月11日",
            FormattedTime = "09:00:00"
        };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { card1, card2, card3 });

        vm.SetGroupingMode("Date");

        var groups = GetPrivateGroups(vm);
        Assert.Equal(2, groups.Count);
        Assert.True(groups.All(g => g.Header.IsVisible));

        // Group 1 has 2025-05-11 (descending default)
        var latestGroup = groups.First();
        Assert.Equal(new DateTime(2025, 5, 11), latestGroup.Header.GroupDate.Date);
        Assert.Single(latestGroup.AllCards);

        var earlierGroup = groups.Last();
        Assert.Equal(new DateTime(2025, 5, 10), earlierGroup.Header.GroupDate.Date);
        Assert.Equal(2, earlierGroup.AllCards.Count);
    }

    [Fact]
    public void GroupingMode_Month_GroupsCardsAcrossDaysIntoSingleMonth()
    {
        var vm = CreateCleanViewModel();

        var card1 = new PhotoCardItemViewModel
        {
            Key = "card1",
            PhotoPath = "card1.heic",
            FileName = "IMG_0001",
            DateTaken = new DateTime(2025, 5, 2, 10, 0, 0)
        };
        var card2 = new PhotoCardItemViewModel
        {
            Key = "card2",
            PhotoPath = "card2.heic",
            FileName = "IMG_0002",
            DateTaken = new DateTime(2025, 5, 28, 14, 0, 0)
        };
        var card3 = new PhotoCardItemViewModel
        {
            Key = "card3",
            PhotoPath = "card3.heic",
            FileName = "IMG_0003",
            DateTaken = new DateTime(2025, 6, 1, 9, 0, 0)
        };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { card1, card2, card3 });

        vm.SetGroupingMode("Month");

        var groups = GetPrivateGroups(vm);
        Assert.Equal(2, groups.Count);

        var mayGroup = groups.FirstOrDefault(g => g.Header.GroupDate.Month == 5);
        Assert.NotNull(mayGroup);
        Assert.Equal(2, mayGroup.AllCards.Count);

        var juneGroup = groups.FirstOrDefault(g => g.Header.GroupDate.Month == 6);
        Assert.NotNull(juneGroup);
        Assert.Single(juneGroup.AllCards);
    }

    [Fact]
    public void GroupingMode_Year_GroupsCardsAcrossMonthsIntoSingleYear()
    {
        var vm = CreateCleanViewModel();

        var card1 = new PhotoCardItemViewModel
        {
            Key = "card1",
            PhotoPath = "card1.heic",
            FileName = "IMG_0001",
            DateTaken = new DateTime(2024, 1, 15, 10, 0, 0)
        };
        var card2 = new PhotoCardItemViewModel
        {
            Key = "card2",
            PhotoPath = "card2.heic",
            FileName = "IMG_0002",
            DateTaken = new DateTime(2024, 12, 31, 14, 0, 0)
        };
        var card3 = new PhotoCardItemViewModel
        {
            Key = "card3",
            PhotoPath = "card3.heic",
            FileName = "IMG_0003",
            DateTaken = new DateTime(2025, 3, 1, 9, 0, 0)
        };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { card1, card2, card3 });

        vm.SetGroupingMode("Year");

        var groups = GetPrivateGroups(vm);
        Assert.Equal(2, groups.Count);

        var year2024 = groups.FirstOrDefault(g => g.Header.GroupDate.Year == 2024);
        Assert.NotNull(year2024);
        Assert.Equal(2, year2024.AllCards.Count);

        var year2025 = groups.FirstOrDefault(g => g.Header.GroupDate.Year == 2025);
        Assert.NotNull(year2025);
        Assert.Single(year2025.AllCards);
    }

    [Fact]
    public void GroupingMode_None_PresentsSingleGroupWithHiddenHeader()
    {
        var vm = CreateCleanViewModel();

        var card1 = new PhotoCardItemViewModel { Key = "1", PhotoPath = "1.heic", DateTaken = DateTime.Now };
        var card2 = new PhotoCardItemViewModel { Key = "2", PhotoPath = "2.heic", DateTaken = DateTime.Now };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { card1, card2 });

        vm.SetGroupingMode("None");

        var groups = GetPrivateGroups(vm);
        Assert.Single(groups);
        Assert.False(groups[0].Header.IsVisible);
        Assert.Equal(2, groups[0].AllCards.Count);

        // Header should not be in FlattenedDisplayItems
        Assert.DoesNotContain(vm.FlattenedDisplayItems, item => item is TimelineHeaderItemViewModel);
        // But rows of cards should be present
        Assert.Contains(vm.FlattenedDisplayItems, item => item is PhotoGridRowViewModel);
    }

    [Fact]
    public void Sorting_Name_OrdersCardsAlphabetically()
    {
        var vm = CreateCleanViewModel();

        var cardB = new PhotoCardItemViewModel { Key = "B", PhotoPath = "B.heic", FileName = "B_File", DateTaken = new DateTime(2025, 1, 1) };
        var cardA = new PhotoCardItemViewModel { Key = "A", PhotoPath = "A.heic", FileName = "A_File", DateTaken = new DateTime(2025, 1, 2) };
        var cardC = new PhotoCardItemViewModel { Key = "C", PhotoPath = "C.heic", FileName = "C_File", DateTaken = new DateTime(2025, 1, 3) };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { cardB, cardA, cardC });

        vm.SetSortMode("Name");
        vm.SetSortDirection(true); // Ascending

        vm.SetGroupingMode("None");
        var groups = GetPrivateGroups(vm);
        var ordered = groups[0].AllCards.Select(c => c.FileName).ToList();
        Assert.Equal(["A_File", "B_File", "C_File"], ordered);

        vm.SetSortDirection(false); // Descending
        groups = GetPrivateGroups(vm);
        ordered = groups[0].AllCards.Select(c => c.FileName).ToList();
        Assert.Equal(["C_File", "B_File", "A_File"], ordered);
    }

    [Fact]
    public void ToggleAllCollapse_TogglesAllGroupHeaders()
    {
        var vm = CreateCleanViewModel();

        var card1 = new PhotoCardItemViewModel { Key = "1", PhotoPath = "1.heic", DateTaken = new DateTime(2025, 1, 1) };
        var card2 = new PhotoCardItemViewModel { Key = "2", PhotoPath = "2.heic", DateTaken = new DateTime(2025, 2, 1) };

        SetPrivateField(vm, "_allCards", new List<PhotoCardItemViewModel> { card1, card2 });

        vm.SetGroupingMode("Month");
        var groups = GetPrivateGroups(vm);
        Assert.Equal(2, groups.Count);
        Assert.True(groups.All(g => !g.Header.IsCollapsed));

        // First toggle collapses all
        vm.ToggleAllCollapse();
        Assert.True(groups.All(g => g.Header.IsCollapsed));

        // Second toggle expands all
        vm.ToggleAllCollapse();
        Assert.True(groups.All(g => !g.Header.IsCollapsed));
    }
}
