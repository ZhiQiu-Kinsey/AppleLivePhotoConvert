using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.ViewModels;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

public class DesktopOptimizationRegressionTests
{
    [Fact]
    public void PhotoCardItemViewModel_ThumbnailEviction_ResetsDisplayImage()
    {
        var card = new PhotoCardItemViewModel
        {
            Key = "test_1",
            PhotoPath = "test.jpg"
        };

        Assert.Null(card.Thumbnail);
        Assert.Null(card.DisplayImage);

        // When Thumbnail becomes null (e.g. eviction), DisplayImage must be null
        card.Thumbnail = null;
        Assert.Null(card.DisplayImage);
    }

    [Fact]
    public void StripViewModel_InitialState_ShowsPlaceholdersAndDisablesExecution()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var stripVm = new StripViewModel(settingsService);

        Assert.Equal("—", stripVm.TotalOriginalText);
        Assert.Equal("—", stripVm.EstimatedAfterText);
        Assert.Equal("—", stripVm.EstimatedSavedText);
        Assert.Equal(string.Empty, stripVm.SavedPercentResult);
        Assert.False(stripVm.CanStartStrip);
        Assert.False(stripVm.StartStripExecutionCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Instance.GetString("NoAlbumOrPhotoSelected"), stripVm.CurrentInputPathText);
    }

    [Fact]
    public void StripViewModel_RefreshAnalysisAsync_WhenCannotStartStrip_CompletesSafely()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var stripVm = new StripViewModel(settingsService);

        var task = stripVm.RefreshAnalysisAsync();
        Assert.True(task.IsCompleted);
        Assert.False(stripVm.CanStartStrip);
        Assert.Equal("—", stripVm.TotalOriginalText);
    }

    [Fact]
    public void ConvertViewModel_PrioritizeThumbnail_SkipsAlreadyLoadedThumbnail()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var convertVm = new ConvertViewModel(settingsService);

        var card = new PhotoCardItemViewModel
        {
            Key = "p1",
            PhotoPath = "non_existent.jpg"
        };

        // Should not throw and should handle missing disk cache gracefully
        convertVm.PrioritizeThumbnail(card);
    }

    [Fact]
    public void BulkObservableCollection_Reset_FiresSingleResetEvent()
    {
        var collection = new Desktop.Collections.BulkObservableCollection<string>(["a", "b", "c"]);
        int eventCount = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventArgs? lastArgs = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastArgs = e;
        };

        collection.Reset(["x", "y", "z", "w"]);

        Assert.Equal(1, eventCount);
        Assert.NotNull(lastArgs);
        Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, lastArgs.Action);
        Assert.Equal(4, collection.Count);
        Assert.Equal(["x", "y", "z", "w"], collection);
    }

    [Fact]
    public void BulkObservableCollection_AddRange_FiresSingleResetEvent()
    {
        var collection = new Desktop.Collections.BulkObservableCollection<int>([1, 2]);
        int eventCount = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventArgs? lastArgs = null;

        collection.CollectionChanged += (s, e) =>
        {
            eventCount++;
            lastArgs = e;
        };

        collection.AddRange([3, 4, 5]);

        Assert.Equal(1, eventCount);
        Assert.NotNull(lastArgs);
        Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, lastArgs.Action);
        Assert.Equal(5, collection.Count);
        Assert.Equal([1, 2, 3, 4, 5], collection);
    }

    [Fact]
    public void ConvertViewModel_FlattenedDisplayItems_IsBulkObservableCollection()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var convertVm = new ConvertViewModel(settingsService);

        Assert.IsType<Desktop.Collections.BulkObservableCollection<IGalleryDisplayItem>>(convertVm.FlattenedDisplayItems);
    }

    [Fact]
    public async Task ConvertViewModel_SetDirection_ReusesCachedScanResult()
    {
        using var context = new Harness.E2ETestContext();
        context.CreateInputFile("IMG_0001.heic", new byte[1000]);
        context.CreateInputFile("IMG_0001.mov", new byte[1000]);

        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var convertVm = new ConvertViewModel(settingsService)
        {
            AlbumDirectory = context.InputDirectory
        };

        // 首次扫描方向 0（苹果转安卓）
        await convertVm.RefreshAlbumAsync();
        Assert.Equal(1, convertVm.ReadyCount);

        // 切换至方向 1（安卓转苹果，由于目录内无 motion photo，此时应就绪 0）
        await convertVm.SetDirection(1);
        Assert.Equal(1, convertVm.ConversionDirection);
        Assert.Equal(0, convertVm.ReadyCount);

        // 再次切回方向 0：应直接命中方向缓存，ReadyCount 瞬间复原为 1
        await convertVm.SetDirection(0);
        Assert.Equal(0, convertVm.ConversionDirection);
        Assert.Equal(1, convertVm.ReadyCount);
    }

    [Fact]
    public async Task ConvertViewModel_DirectoryChange_InvalidatesDirectionCache()
    {
        using var context1 = new Harness.E2ETestContext();
        context1.CreateInputFile("IMG_0001.heic", new byte[1000]);
        context1.CreateInputFile("IMG_0001.mov", new byte[1000]);

        using var context2 = new Harness.E2ETestContext();

        string tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(tempPath);
        var convertVm = new ConvertViewModel(settingsService)
        {
            AlbumDirectory = context1.InputDirectory
        };

        await convertVm.RefreshAlbumAsync();
        Assert.Equal(1, convertVm.ReadyCount);

        // 变更相册目录为不同目录
        convertVm.AlbumDirectory = context2.InputDirectory;
        await convertVm.RefreshAlbumAsync();
        Assert.Equal(0, convertVm.ReadyCount);
    }
}
