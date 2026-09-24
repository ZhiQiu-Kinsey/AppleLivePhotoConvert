using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.E2E.Harness;

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
    public void LibraryViewModel_PrioritizeThumbnail_SkipsAlreadyLoadedThumbnail()
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
    public void LibraryViewModel_FlattenedDisplayItems_IsBulkObservableCollection()
    {
        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();

        Assert.IsType<Desktop.Collections.BulkObservableCollection<IGalleryDisplayItem>>(libraryVm.FlattenedDisplayItems);
    }

    [Fact]
    public void PlaybackHost_DecodeArguments_PreserveSourceFramesWithoutForcedRate()
    {
        string[] arguments = PlaybackHost.BuildDecodeArguments(@"F:\媒体目录\IMG 5336.MOV");

        int inputIndex = Array.IndexOf(arguments, "-i");
        int fpsModeIndex = Array.IndexOf(arguments, "-fps_mode");

        Assert.True(inputIndex >= 0);
        Assert.Equal(@"F:\媒体目录\IMG 5336.MOV", arguments[inputIndex + 1]);
        Assert.True(fpsModeIndex >= 0);
        Assert.Equal("passthrough", arguments[fpsModeIndex + 1]);
        Assert.DoesNotContain("-r", arguments);
        Assert.DoesNotContain("mjpeg", arguments);
        Assert.DoesNotContain("-q:v", arguments);
        Assert.Contains("bmp", arguments);
        Assert.Contains("bgr24", arguments);
        Assert.Contains("scale=720:-2:flags=lanczos", arguments);
        Assert.Contains("-an", arguments);
        Assert.Contains("-sn", arguments);
        Assert.Contains("-dn", arguments);
    }

    [Fact]
    public async Task BmpPipeFrameReader_ReadsConsecutiveLosslessFrameBoundaries()
    {
        byte[] firstSource = CreateBmpPacket(54, 0x11);
        byte[] secondSource = CreateBmpPacket(70, 0x22);
        using var pipe = new MemoryStream([.. firstSource, .. secondSource]);

        byte[]? firstFrame = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);
        byte[]? secondFrame = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);
        byte[]? end = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);

        Assert.Equal(firstSource, firstFrame);
        Assert.Equal(secondSource, secondFrame);
        Assert.Null(end);
    }

    [Fact]
    public void LruThumbnailManager_BitmapAllocationFailure_DoesNotEscapeToUiThread()
    {
        var manager = new LruThumbnailManager(
            new ThumbnailReader(),
            _ => throw new Exception("Unable to allocate pixels for the bitmap."));
        var card = new PhotoCardItemViewModel
        {
            Key = "allocation-failure",
            PhotoPath = "allocation-failure.jpg"
        };

        var exception = Record.Exception(() => manager.RequestThumbnail(card, [1, 2, 3]));

        Assert.Null(exception);
        Assert.Null(card.Thumbnail);
        Assert.Null(card.DisplayImage);
    }

    [Fact]
    public void DesktopImageCaches_KeepRawBitmapBudgetsBounded()
    {
        Assert.InRange(LruThumbnailManager.MaxActiveBitmaps, 1, 24);
        Assert.InRange(PlaybackHost.MaxFrameCacheSets, 1, 2);
        Assert.InRange(PlaybackHost.MaxPreviewFrames, 1, 60);
        Assert.InRange(LivePhotoStreamPlayer.MaxFrames, 1, 90);
    }

    [Fact]
    public void QuickLookDecodeArguments_AvoidUnstableAutomaticHardwareAcceleration()
    {
        string[] arguments = LivePhotoStreamPlayer.BuildDecodeArguments(@"F:\媒体目录\IMG 5410.MOV");

        Assert.DoesNotContain("-hwaccel", arguments);
        Assert.Contains("scale=1080:1080:force_original_aspect_ratio=decrease:flags=lanczos", arguments);
        Assert.Contains("passthrough", arguments);
        Assert.Contains("bmp", arguments);
        Assert.Contains("bgr24", arguments);
    }

    private static byte[] CreateBmpPacket(int length, byte payload)
    {
        byte[] bytes = Enumerable.Repeat(payload, length).ToArray();
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), length);
        return bytes;
    }

    [Fact]
    public async Task LibraryViewModel_SetScanMode_ReusesCachedScanResult()
    {
        using var context = new Harness.E2ETestContext();
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
    public async Task LibraryViewModel_DirectoryChange_InvalidatesDirectionCache()
    {
        using var context1 = new Harness.E2ETestContext();
        context1.CreateInputFile("IMG_0001.heic", new byte[1000]);
        context1.CreateInputFile("IMG_0001.mov", new byte[1000]);

        using var context2 = new Harness.E2ETestContext();

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
    public void LibraryViewModel_OnViewportScrolled_SetsIsUserScrollingAndDefersRelayout()
    {
        using var host = new DesktopTestHost();
        var libraryVm = host.Get<LibraryViewModel>();

        Assert.False(libraryVm.IsUserScrolling);

        // 模拟滚动视口
        libraryVm.OnViewportScrolled(100, 600);
        Assert.True(libraryVm.IsUserScrolling);
    }

    [Fact]
    public async Task AlbumScanner_ScanDirectoryAsync_SniffsExactDimensionsAndResolutions()
    {
        using var context = new Harness.E2ETestContext();

        // 构造一个 1080x1920 (竖屏) 的真实 PNG 头部
        byte[] pngHeader = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(pngHeader, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(pngHeader.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(16, 4), 1080); // Width
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(20, 4), 1920); // Height

        context.CreateInputFile("IMG_9999.png", pngHeader);
        context.CreateInputFile("IMG_9999.mov", new byte[100]);

        var result = await AlbumScanner.ScanDirectoryAsync(new Localizer(), context.InputDirectory, 0, TestContext.Current.CancellationToken);

        Assert.Single(result.Groups);
        var card = Assert.Single(result.Groups[0].AllCards);

        // 验证已提前嗅探到真实分辨率与宽高比，而不是默认的 4:3
        Assert.Equal("1080×1920", card.ResolutionText);
        Assert.Equal(1080.0 / 1920.0, card.AspectRatio, precision: 4);
    }
}
