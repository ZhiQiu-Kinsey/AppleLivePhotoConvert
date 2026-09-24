using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Tests.Services;

/// <summary>原始位图是非托管内存：各处缓存必须有界。</summary>
public class ThumbnailMemoryTests
{
    [Fact]
    public void DesktopImageCaches_KeepRawBitmapBudgetsBounded()
    {
        Assert.Equal(192L * 1024 * 1024, ThumbnailPipeline.DefaultBudgetBytes);
        Assert.Equal(ThumbnailPipeline.DefaultBudgetBytes, new GalleryPreferences().ThumbnailBudgetBytes);
        Assert.InRange(PlaybackHost.MaxFrameCacheSets, 1, 2);
        Assert.InRange(PlaybackHost.MaxPreviewFrames, 1, 60);
        Assert.InRange(LivePhotoStreamPlayer.MaxFrames, 1, 90);
    }

    [Theory]
    [InlineData(0, GalleryPreferences.MinThumbnailBudgetMb)]
    [InlineData(100_000, GalleryPreferences.MaxThumbnailBudgetMb)]
    [InlineData(256, 256)]
    public void ThumbnailBudget_IsClampedToUsableRange(int configuredMb, int expectedMb)
    {
        var gallery = new GalleryPreferences { ThumbnailBudgetMb = configuredMb };
        Assert.Equal(expectedMb * 1024L * 1024, gallery.ThumbnailBudgetBytes);
    }
}
