using ImageMagick;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class QuickLookThumbnailSourceTests
{
    [Fact]
    public void TryCreate_OnlyOnMac() =>
        Assert.Equal(OperatingSystem.IsMacOS(), QuickLookThumbnailSource.TryCreate() is not null);

    [Fact]
    public async Task Jpeg_KeepsOrientationAndOpaqueColor()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "快速查看缩略图仅在 macOS 上可用。");

        using var dir = new TempDirectory();
        using var photo = new MagickImage(MagickColors.Blue, 400, 400);
        using var topHalf = new MagickImage(MagickColors.Red, 400, 200);
        photo.Composite(topHalf, 0, 0, CompositeOperator.SrcOver);
        photo.Format = MagickFormat.Jpeg;
        photo.Quality = 95;
        var path = dir.CreateFile("ql.jpg", photo.ToByteArray());
        var source = new QuickLookThumbnailSource();

        using var image = await source.TryGetAsync(path, 200, 200, TestContext.Current.CancellationToken);

        Assert.NotNull(image);
        Assert.False(image.HasAlpha);
        Assert.True(image.Width >= 180 && image.Height >= 180);
        var top = ThumbnailTestImages.PixelAt(image, (int)image.Width / 2, (int)image.Height / 5);
        var bottom = ThumbnailTestImages.PixelAt(image, (int)image.Width / 2, (int)image.Height * 4 / 5);
        Assert.True(top.R > 180 && top.B < 80, $"顶部应是红色 {top}");
        Assert.True(bottom.B > 180 && bottom.R < 80, $"底部应是蓝色 {bottom}");

        using var cancel = new CancellationTokenSource();
        var pending = source.TryGetAsync(path, 200, 200, cancel.Token);
        await cancel.CancelAsync();
        try
        {
            using var cancelled = await pending;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task MissingOrNonPhoto_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "快速查看缩略图仅在 macOS 上可用。");

        using var dir = new TempDirectory();
        var text = dir.CreateFile("note.txt", "hello"u8.ToArray());
        var source = new QuickLookThumbnailSource();

        Assert.Null(await source.TryGetAsync(dir.Combine("missing.jpg"), 256, 256, TestContext.Current.CancellationToken));
        Assert.Null(await source.TryGetAsync(text, 256, 256, TestContext.Current.CancellationToken));
    }
}
