using LivePhotoConvert.Desktop.Collections;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Library.Gallery;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

public class LibraryViewModelTests
{
    [Fact]
    public void ViewPreferences_ArePersistedAndRestoredOnNextStart()
    {
        string settingsPath;
        using (var first = new DesktopTestHost())
        {
            var layout = first.Get<LibraryViewModel>().Layout;
            Assert.Equal(("DateTaken", false, "Date", "Medium", "Natural"),
                (layout.SortMode, layout.IsSortAscending, layout.GroupingMode, layout.ScaleMode, layout.CropMode));

            layout.SetSortMode("Name");
            layout.SetSortDirection(true);
            layout.SetGroupingMode("Month");
            layout.SetScaleMode("Large");
            layout.SetCropMode("Square");

            var saved = first.Settings.Current.Gallery;
            Assert.Equal(("Name", true, "Month", "Large", "Square"), (saved.SortMode, saved.SortAscending, saved.Grouping, saved.Scale, saved.Crop));
            first.Settings.Flush();
            settingsPath = Path.Combine(Path.GetTempPath(), $"lpc_prefs_{Guid.NewGuid():N}.json");
            File.Copy(first.SettingsPath, settingsPath);
        }

        try
        {
            using var second = new DesktopTestHost(services => services.AddSingleton(_ => new SettingsStore(settingsPath)));
            var restored = second.Get<LibraryViewModel>().Layout;
            Assert.Equal(("Name", true, "Month", "Large", "Square"),
                (restored.SortMode, restored.IsSortAscending, restored.GroupingMode, restored.ScaleMode, restored.CropMode));
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void ViewPreferences_UnknownValuesFallBackToDefaults()
    {
        using var host = new DesktopTestHost();
        host.Settings.Update(s =>
        {
            s.Gallery.SortMode = "Random";
            s.Gallery.Scale = "Huge";
            s.Gallery.Grouping = "";
            s.Gallery.Crop = "Circle";
        });

        var layout = host.Get<LibraryViewModel>().Layout;

        Assert.Equal(("DateTaken", "Date", "Medium", "Natural"), (layout.SortMode, layout.GroupingMode, layout.ScaleMode, layout.CropMode));
        layout.SetScaleMode("Gigantic");
        Assert.Equal("Medium", layout.ScaleMode);
    }

    /// <summary>设置里的布尔字符串按值解析："false" 就是 false。</summary>
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void SortDirection_ParsesStringParameters(string parameter, bool expected)
    {
        using var host = new DesktopTestHost();
        var layout = host.Get<LibraryViewModel>().Layout;
        layout.SetSortDirection(!expected);

        layout.SetSortDirection(parameter);

        Assert.Equal(expected, layout.IsSortAscending);
        Assert.Equal(expected, host.Settings.Current.Gallery.SortAscending);
    }

    [Fact]
    public async Task SelectAllVisible_WithFalseString_Deselects()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        fixture.AddApplePair("IMG_0002");
        await fixture.ScanAsync();

        fixture.Library.SelectAllVisible("false");

        Assert.All(fixture.Library.AllCards, c => Assert.False(c.IsSelected));
    }

    [Fact]
    public async Task SquareCrop_IsAppliedToFreshlyScannedCards()
    {
        using var fixture = new InspectorFixture(s => s.Gallery.Crop = "Square");
        fixture.AddApplePair("IMG_0001");

        await fixture.ScanAsync();

        Assert.All(fixture.Library.AllCards, c => Assert.Equal(c.PreviewHeight, c.DisplayWidth - GalleryMetrics.CardHorizontalChrome, 6));
    }

    [Fact]
    public async Task Selection_DrivesSelectedOrAllCardsFocusAndSingleEventPerBulkChange()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        fixture.AddApplePair("IMG_0002");
        fixture.AddApplePair("IMG_0003");
        var library = fixture.Library;
        var events = 0;
        library.SelectionChanged += (_, _) => events++;

        await fixture.ScanAsync();
        Assert.Equal(1, events);
        Assert.Equal(3, library.AllCards.Count);
        Assert.Equal(3, library.Selection.SelectedCount);

        library.SelectAllVisible(false);
        Assert.Equal(2, events);
        Assert.Equal(0, library.Selection.SelectedCount);
        Assert.Same(library.AllCards, library.SelectedOrAllCards);

        var second = library.AllCards[1];
        second.ToggleSelectCommand.Execute(null);
        Assert.Equal(3, events);
        Assert.Equal([second], library.SelectedOrAllCards);
        Assert.Same(second, library.FocusedCard);

        library.SelectAllVisible(true);
        Assert.Equal(4, events);
        Assert.Equal(3, library.SelectedOrAllCards.Count);
        Assert.Same(second, library.FocusedCard);
    }

    [Fact]
    public async Task AlbumDirectory_IsTheSingleSharedLocationAcrossActions()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        fixture.FakePickFolder(fixture.Album.InputDirectory);

        await fixture.Library.SelectAlbumFolderCommand.ExecuteAsync(null);

        Assert.Equal(fixture.Album.InputDirectory, fixture.Host.Settings.Current.LastScanDirectory);
        fixture.Inspector.SetActionCommand.Execute("Strip");
        Assert.Equal(fixture.Album.InputDirectory, fixture.Library.AlbumDirectory);
        Assert.Equal(fixture.Album.InputDirectory, fixture.Host.Settings.Current.LastScanDirectory);
    }

    [Fact]
    public void Library_UsesTheSharedThumbnailPipelineAndBulkItems()
    {
        using var host = new DesktopTestHost();
        var library = host.Get<LibraryViewModel>();

        Assert.Same(host.Get<IThumbnailPipeline>(), library.Thumbnails);
        Assert.Same(host.Get<LibraryCatalog>(), library.Catalog);
        Assert.IsType<BulkObservableCollection<IGalleryDisplayItem>>(library.Layout.Items);
    }

    /// <summary>切换动作只在已扫描的条目中筛选：扫描一次，删掉源文件也不影响结果。</summary>
    [Fact]
    public async Task SwitchingAction_FiltersWithoutRescanning()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        fixture.AddMotionPhoto("MVIMG_0002");
        fixture.Album.CreateInputFile("IMG_0003.jpg", Core.Tests.Support.SyntheticMedia.Jpeg());
        var library = fixture.Library;
        await fixture.ScanAsync();
        Assert.Equal(1, library.Catalog.ScanCount);
        Assert.Equal(3, library.Catalog.Cards.Count);
        Assert.Equal(["IMG_0001"], library.AllCards.Select(c => c.FileName));

        foreach (var file in Directory.GetFiles(fixture.Album.InputDirectory))
        {
            File.Delete(file);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        fixture.Inspector.SetActionCommand.Execute(nameof(ConversionAction.ToApple));
        watch.Stop();
        Assert.Equal(ConversionAction.ToApple, library.ActionFilter);
        Assert.Equal(["MVIMG_0002"], library.AllCards.Select(c => c.FileName));
        Assert.Equal(["MVIMG_0002"], library.Layout.DisplayedCards.Select(c => c.FileName));

        fixture.Inspector.SetActionCommand.Execute(nameof(ConversionAction.Strip));
        Assert.Equal(["IMG_0001", "MVIMG_0002"], library.AllCards.Select(c => c.FileName));
        fixture.Inspector.SetActionCommand.Execute(nameof(ConversionAction.ToAndroid));
        Assert.Equal(["IMG_0001"], library.AllCards.Select(c => c.FileName));
        Assert.Equal(1, library.Catalog.ScanCount);
        TestContext.Current.TestOutputHelper?.WriteLine($"切换动作耗时 {watch.Elapsed.TotalMilliseconds:F2} ms");
    }

    /// <summary>1 万张卡片（实况对与动态照片各半）来回切换动作：只筛选与重排，不读盘。</summary>
    [Fact]
    public async Task SwitchingAction_OnTenThousandCards_IsFast()
    {
        List<Core.Media.LibraryItem> items = [.. Enumerable.Range(0, 10_000).Select(i => i % 2 == 0
            ? Cards.ApplePairItem($"IMG_{i:D5}", aspect: i % 3 == 0 ? 0.75 : 4.0 / 3.0, taken: new DateTime(2025, 1, 1).AddMinutes(i))
            : Cards.MotionPhoto($"MVIMG_{i:D5}").Item with { CaptureTimeLocal = new DateTime(2025, 1, 1).AddMinutes(i) })];
        using var host = new DesktopTestHost(services => services.AddSingleton(sp => new LibraryCatalog(
            sp.GetRequiredService<ILocalizer>(), sp.GetRequiredService<ILibraryEnricher>(),
            (_, _, _) => Task.FromResult(new Core.Media.LibraryScanResult(items, 20_000, 0)))));
        var library = host.Get<LibraryViewModel>();
        library.AlbumDirectory = "/album";
        await library.RefreshAlbumAsync();
        Assert.Equal(5_000, library.AllCards.Count);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        library.SetActionFilter(ConversionAction.ToApple);
        library.SetActionFilter(ConversionAction.Strip);
        library.SetActionFilter(ConversionAction.ToAndroid);
        watch.Stop();

        Assert.Equal(1, library.Catalog.ScanCount);
        Assert.Equal(5_000, library.Layout.DisplayedCards.Count);
        TestContext.Current.TestOutputHelper?.WriteLine($"1 万张卡片切换 3 次动作共 {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"切换动作耗时 {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task DirectoryChange_Rescans()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        using var empty = new TestSandbox();
        var library = fixture.Library;

        await fixture.ScanAsync();
        Assert.Equal(1, library.Selection.ReadyCount);

        library.AlbumDirectory = empty.InputDirectory;
        await library.RefreshAlbumAsync();
        Assert.Equal(0, library.Selection.ReadyCount);
        Assert.Equal(2, library.Catalog.ScanCount);
        Assert.False(library.HasPhotos);
    }

    [Fact]
    public async Task MissingDirectory_ShowsErrorStateInsteadOfCrashing()
    {
        using var fixture = new InspectorFixture();
        var library = fixture.Library;
        var missing = Path.Combine(fixture.Album.RootDirectory, "gone");

        library.AlbumDirectory = missing;
        await library.RefreshAlbumAsync();

        Assert.Equal(LibraryScanError.NotFound, library.Catalog.Error);
        Assert.Equal(fixture.Host.Localizer["ScanErrorTitle"], library.EmptyStateTitle);
        Assert.Equal(fixture.Host.Localizer.Format("ScanErrorNotFoundFormat", missing), library.EmptyStateSubtitle);
        Assert.False(library.HasPhotos);
    }

    /// <summary>人工裁决通过：卡片不再待裁决（徽章切换）、被选中，就绪与待裁决计数随之更新。</summary>
    [Fact]
    public async Task Arbitration_UpdatesCountsBadgeAndSelection()
    {
        using var fixture = new InspectorFixture();
        fixture.AddApplePair("IMG_0001");
        fixture.AddReviewPair("IMG_0002");
        var library = fixture.Library;
        await fixture.ScanAsync();
        var card = library.AllCards.Single(c => c.FileName == "IMG_0002");
        Assert.True(card.RequiresPairReview);
        Assert.False(card.IsSelected);
        Assert.Equal((1, 1, 1), (library.Selection.ReadyCount, library.Selection.SuspiciousCount, library.Selection.SelectedCount));
        Assert.Equal(1, fixture.Inspector.ApplicableCount);
        var events = 0;
        var badgeChanges = 0;
        library.SelectionChanged += (_, _) => events++;
        card.PropertyChanged += (_, e) => badgeChanges += e.PropertyName == nameof(PhotoCardItemViewModel.RequiresPairReview) ? 1 : 0;

        var arbitration = library.ArbitrateCommand.ExecuteAsync(card);
        var dialog = Assert.IsType<ArbitrateDialogViewModel>(fixture.Dialogs.Current);
        Assert.Equal(11, dialog.TimeDiffSeconds, 3);
        dialog.ConfirmWhitelistCommand.Execute(null);
        await arbitration;

        Assert.False(card.RequiresPairReview);
        Assert.True(card.IsForceAccepted);
        Assert.True(card.IsSelected);
        Assert.Equal(1, badgeChanges);
        Assert.Equal(1, events);
        Assert.Same(card, library.FocusedCard);
        Assert.Equal((2, 0, 2), (library.Selection.ReadyCount, library.Selection.SuspiciousCount, library.Selection.SelectedCount));
        Assert.Equal(2, fixture.Inspector.ApplicableCount);
    }

    [Fact]
    public void OnViewportScrolled_SetsIsUserScrolling()
    {
        using var host = new DesktopTestHost();
        var library = host.Get<LibraryViewModel>();

        Assert.False(library.IsUserScrolling);
        library.OnViewportScrolled(100, 600);
        Assert.True(library.IsUserScrolling);
    }
}

internal static class InspectorFixtureExtensions
{
    public static void FakePickFolder(this InspectorFixture fixture, string folder) => fixture.Host.FilePicker.NextResult = folder;
}
