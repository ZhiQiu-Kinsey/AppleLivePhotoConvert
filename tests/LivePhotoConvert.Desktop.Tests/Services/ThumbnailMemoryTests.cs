using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Tests.Services;

/// <summary>原始位图是非托管内存：缓存必须有界，分配失败必须降级。</summary>
public class ThumbnailMemoryTests
{
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
}
