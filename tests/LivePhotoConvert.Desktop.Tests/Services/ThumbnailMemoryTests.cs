using LivePhotoConvert.Desktop.Features.Library.Thumbnails;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Tests.Services;

/// <summary>原始位图是非托管内存：各处缓存必须有界。</summary>
public class ThumbnailMemoryTests
{
    [Fact]
    public void DesktopImageCaches_KeepRawBitmapBudgetsBounded()
    {
        Assert.Equal(192L * 1024 * 1024, ThumbnailPipeline.DefaultBudgetBytes);
        Assert.Equal(ThumbnailPipeline.DefaultBudgetBytes, new GalleryPreferences().ThumbnailBudgetBytes);
        Assert.Equal(96L * 1024 * 1024, PlaybackBudget.Hover.Bytes);
        Assert.Equal(256L * 1024 * 1024, PlaybackBudget.QuickLook.Bytes);
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
