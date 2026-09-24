using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class ThumbnailTiersTests
{
    [Theory]
    [InlineData(0, 256)]
    [InlineData(1, 256)]
    [InlineData(256, 256)]
    [InlineData(257, 384)]
    [InlineData(325, 384)]
    [InlineData(488, 512)]
    [InlineData(769, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1536)]
    [InlineData(1600, 2048)]
    [InlineData(5000, 2048)]
    public void Select_ReturnsSmallestTierNotBelowRequirement(int required, int expected) =>
        Assert.Equal(expected, ThumbnailTiers.Select(required));

    [Fact]
    public void Heights_AreAscendingAndIsTierMatches()
    {
        Assert.Equal([256, 384, 512, 768, 1024, 1536, 2048], ThumbnailTiers.Heights.ToArray());
        Assert.True(ThumbnailTiers.IsTier(512));
        Assert.False(ThumbnailTiers.IsTier(500));
    }

    [Theory]
    [InlineData(3000, 4000, 384, 288, 384)]      // 竖图：高度等于档位
    [InlineData(4000, 3000, 1024, 1365, 1024)]
    [InlineData(200, 150, 256, 200, 150)]        // 小图不放大
    [InlineData(20000, 1000, 1024, 4096, 205)]   // 超宽全景受长边上限约束
    [InlineData(1000, 20000, 1024, 51, 1024)]
    [InlineData(5000, 5000, 5000, 4096, 4096)]
    [InlineData(6000, 4000, 2048, 3072, 2048)]   // 最高档位
    [InlineData(12000, 4000, 2048, 4096, 1365)]  // 最高档位仍受长边上限约束
    public void Fit_ShrinksToTierAndLongEdgeCap(int width, int height, int tier, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ThumbnailTiers.Fit(width, height, tier));
}
