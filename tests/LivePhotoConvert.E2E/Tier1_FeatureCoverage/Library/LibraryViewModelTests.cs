using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.E2E.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage.Library;

public class LibraryViewModelTests
{
    [Fact]
    public void ViewPreferences_ArePersistedAndRestoredOnNextStart()
    {
        string settingsPath;
        using (var first = new DesktopTestHost())
        {
            var library = first.Get<LibraryViewModel>();
            Assert.Equal(("DateTaken", false, "Date", "Medium", "Natural"),
                (library.SortMode, library.IsSortAscending, library.GroupingMode, library.ScaleMode, library.CropMode));

            library.SetSortMode("Name");
            library.SetSortDirection(true);
            library.SetGroupingMode("Month");
            library.SetScaleMode("Large");
            library.SetCropMode("Square");

            var saved = first.Settings.Current.Gallery;
            Assert.Equal(("Name", true, "Month", "Large", "Square"), (saved.SortMode, saved.SortAscending, saved.Grouping, saved.Scale, saved.Crop));
            first.Settings.Flush();
            settingsPath = Path.Combine(Path.GetTempPath(), $"lpc_prefs_{Guid.NewGuid():N}.json");
            File.Copy(first.SettingsPath, settingsPath);
        }

        try
        {
            using var second = new DesktopTestHost(services => services.AddSingleton(_ => new SettingsStore(settingsPath)));
            var restored = second.Get<LibraryViewModel>();
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

        var library = host.Get<LibraryViewModel>();

        Assert.Equal(("DateTaken", "Date", "Medium", "Natural"), (library.SortMode, library.GroupingMode, library.ScaleMode, library.CropMode));
        library.SetScaleMode("Gigantic");
        Assert.Equal("Medium", library.ScaleMode);
    }

    [Fact]
    public async Task CropMode_IsAppliedToFreshlyScannedCards()
    {
        using var fixture = new InspectorFixture(s => s.Gallery.Crop = "Square");
        fixture.AddApplePair("IMG_0001");

        await fixture.ScanAsync();

        Assert.All(fixture.Library.AllCards, c => Assert.True(c.IsSquareCrop));
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
        Assert.Equal(3, library.SelectedCount);

        library.SelectAllVisible(false);
        Assert.Equal(2, events);
        Assert.Equal(0, library.SelectedCount);
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
}

internal static class InspectorFixtureExtensions
{
    public static void FakePickFolder(this InspectorFixture fixture, string folder) => fixture.Host.FilePicker.NextResult = folder;
}
