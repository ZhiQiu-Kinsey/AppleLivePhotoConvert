using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Collections;
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

    [Fact]
    public void PrioritizeThumbnail_MissingFile_DoesNotThrow()
    {
        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();

        var card = new PhotoCardItemViewModel
        {
            Key = "p1",
            PhotoPath = "non_existent.jpg"
        };

        // Should not throw and should handle missing disk cache gracefully
        libraryVm.PrioritizeThumbnail(card);
    }

    [Fact]
    public void FlattenedDisplayItems_IsBulkObservableCollection()
    {
        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();

        Assert.IsType<BulkObservableCollection<IGalleryDisplayItem>>(libraryVm.FlattenedDisplayItems);
    }

    [Fact]
    public async Task SetScanMode_ReusesCachedScanResult()
    {
        using var context = new TestSandbox();
        context.CreateInputFile("IMG_0001.heic", new byte[1000]);
        context.CreateInputFile("IMG_0001.mov", new byte[1000]);

        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();
        libraryVm.AlbumDirectory = context.InputDirectory;

        // 首次扫描：苹果实况对模式
        await libraryVm.SetScanModeAsync(ScanModes.ApplePairs);
        await libraryVm.RefreshAlbumAsync();
        Assert.Equal(1, libraryVm.ReadyCount);

        // 切到安卓动态照片模式：目录内没有动态照片
        await libraryVm.SetScanModeAsync(ScanModes.MotionPhotos);
        Assert.Equal(ScanModes.MotionPhotos, libraryVm.ScanMode);
        Assert.Equal(0, libraryVm.ReadyCount);

        // 切回：命中该模式的缓存，删掉源文件也不影响结果
        File.Delete(Path.Combine(context.InputDirectory, "IMG_0001.mov"));
        await libraryVm.SetScanModeAsync(ScanModes.ApplePairs);
        Assert.Equal(ScanModes.ApplePairs, libraryVm.ScanMode);
        Assert.Equal(1, libraryVm.ReadyCount);
    }

    [Fact]
    public async Task DirectoryChange_InvalidatesScanCache()
    {
        using var context1 = new TestSandbox();
        context1.CreateInputFile("IMG_0001.heic", new byte[1000]);
        context1.CreateInputFile("IMG_0001.mov", new byte[1000]);

        using var context2 = new TestSandbox();

        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();
        libraryVm.AlbumDirectory = context1.InputDirectory;

        await libraryVm.RefreshAlbumAsync();
        Assert.Equal(1, libraryVm.ReadyCount);

        // 变更相册目录为不同目录
        libraryVm.AlbumDirectory = context2.InputDirectory;
        await libraryVm.RefreshAlbumAsync();
        Assert.Equal(0, libraryVm.ReadyCount);
    }

    [Fact]
    public void OnViewportScrolled_SetsIsUserScrolling()
    {
        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();

        Assert.False(libraryVm.IsUserScrolling);

        // 模拟滚动视口
        libraryVm.OnViewportScrolled(100, 600);
        Assert.True(libraryVm.IsUserScrolling);
    }
}

internal static class InspectorFixtureExtensions
{
    public static void FakePickFolder(this InspectorFixture fixture, string folder) => fixture.Host.FilePicker.NextResult = folder;
}
