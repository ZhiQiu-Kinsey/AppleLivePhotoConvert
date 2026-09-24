using ImageMagick;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class WindowsShellThumbnailSourceTests
{
    [Fact]
    public void TryCreate_OnlyOnWindows() =>
        Assert.Equal(OperatingSystem.IsWindows(), WindowsShellThumbnailSource.TryCreate() is not null);

    [Fact]
    public async Task Jpeg_ReturnsOpaqueImageOrNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows Shell 缩略图仅在 Windows 上可用");
            return;
        }

        using var dir = new TempDirectory();
        var photo = dir.CreateFile("shell.jpg", ThumbnailTestImages.Jpeg(1600, 1200, MagickColors.Red));
        using var source = new WindowsShellThumbnailSource();

        using var image = await source.TryGetAsync(photo, 683, 512, TestContext.Current.CancellationToken);

        // 无缩略图处理程序的环境（如 Server Core）返回 null；有则必须是按 BGRA 正确解析的不透明红图
        if (image is not null)
        {
            Assert.False(image.HasAlpha);
            Assert.True(image.Width > 0 && image.Height > 0);
            var (r, g, b) = ThumbnailTestImages.PixelAt(image, (int)image.Width / 2, (int)image.Height / 2);
            Assert.True(r > 200 && g < 60 && b < 60, $"像素 {r},{g},{b}");
        }
    }

    [Fact]
    public async Task MissingOrUnsupportedFile_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows Shell 缩略图仅在 Windows 上可用");
            return;
        }

        using var dir = new TempDirectory();
        var text = dir.CreateFile("note.txt", "hello"u8.ToArray());
        using var source = new WindowsShellThumbnailSource();

        Assert.Null(await source.TryGetAsync(dir.Combine("missing.jpg"), 256, 256, TestContext.Current.CancellationToken));
        // THUMBNAILONLY：没有缩略图的类型不能退化成文件图标
        Assert.Null(await source.TryGetAsync(text, 256, 256, TestContext.Current.CancellationToken));
    }
}
