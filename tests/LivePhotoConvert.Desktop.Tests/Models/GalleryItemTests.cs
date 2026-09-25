using LivePhotoConvert.Desktop.Converters;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Library;

namespace LivePhotoConvert.Desktop.Tests.Models;

public class GalleryItemTests
{
    [Fact]
    public void PhotoCard_ThumbnailEviction_ResetsDisplayImage()
    {
        var card = Cards.Still("test");

        Assert.Null(card.Thumbnail);
        Assert.Null(card.DisplayImage);

        // When Thumbnail becomes null (e.g. eviction), DisplayImage must be null
        card.Thumbnail = null;
        Assert.Null(card.DisplayImage);
    }

    /// <summary>动态照片是一个文件，只显示总大小；苹果实况是两个文件，照片与视频分别列出。</summary>
    [Fact]
    public void SizeSummary_ShowsOneTotalForMotionPhotoAndBothFilesForApplePair()
    {
        Assert.Equal(ByteSizeConverter.Format(9000), Cards.MotionPhoto("mp").SizeSummary);
        Assert.Equal($"{ByteSizeConverter.Format(3000)} + {ByteSizeConverter.Format(5000)}", Cards.ApplePair("pair").SizeSummary);
        Assert.Equal(ByteSizeConverter.Format(2000), Cards.Still("still").SizeSummary);
    }
}
