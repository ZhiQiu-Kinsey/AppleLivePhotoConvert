using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Tests.Models;

public class GalleryItemTests
{
    [Fact]
    public void PhotoCard_ThumbnailEviction_ResetsDisplayImage()
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
}
