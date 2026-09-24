using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Dialogs;

/// <summary>对比图解码：显示位图按目标像素解码、细节从原图 1:1 裁切并只缓存预算内的原图块、方向摆正。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class CompareImageSourceTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _sandbox.Dispose();

    [AvaloniaFact]
    public async Task Detail_IsOneToOneCropOfSourcePixels()
    {
        var before = WritePng("before.png", 1024, 768, seed: 1);
        var after = WritePng("after.png", 1024, 768, seed: 2);
        using var source = await CompareImageSource.OpenAsync(before, after, Token);
        var region = new PixelRect(300, 200, 128, 96);

        using var detail = await source.GetDetailAsync(region, region.Size, Token);

        Assert.NotNull(detail);
        Assert.Equal(region, detail.Region);
        Assert.Equal(region.Size, detail.Before.PixelSize);
        Assert.Equal(region.Size, detail.After.PixelSize);
        foreach (var (x, y) in new[] { (0, 0), (127, 95), (64, 10) })
        {
            Assert.Equal(SourcePixel(before, region.X + x, region.Y + y), BitmapPixel(detail.Before, x, y));
            Assert.Equal(SourcePixel(after, region.X + x, region.Y + y), BitmapPixel(detail.After, x, y));
        }
    }

    [AvaloniaFact]
    public async Task Detail_NearbyRegionsHitTileCache_FarRegionsDecodeAgainWithinBudget()
    {
        var before = WritePng("big-before.png", 4096, 3072, seed: 3);
        var after = WritePng("big-after.png", 4096, 3072, seed: 4);
        using var source = await CompareImageSource.OpenAsync(before, after, Token);

        (await source.GetDetailAsync(new PixelRect(100, 100, 128, 128), new PixelSize(128, 128), Token))!.Dispose();
        Assert.Equal(2, source.FullDecodes);
        (await source.GetDetailAsync(new PixelRect(400, 300, 256, 256), new PixelSize(256, 256), Token))!.Dispose();
        Assert.Equal(2, source.FullDecodes);

        // 整图 12.6MP 远超每侧预算，右下角不在缓存里
        (await source.GetDetailAsync(new PixelRect(3900, 2900, 128, 128), new PixelSize(128, 128), Token))!.Dispose();
        Assert.Equal(4, source.FullDecodes);
        Assert.InRange(source.CachedBytes, 1, 2 * CompareImageSource.TileBudgetBytes);
    }

    [AvaloniaFact]
    public async Task Detail_LargerThanOutput_IsDownscaled()
    {
        var path = WritePng("scaled.png", 1600, 1200, seed: 5);
        using var source = await CompareImageSource.OpenAsync(path, path, Token);

        using var detail = await source.GetDetailAsync(new PixelRect(0, 0, 1600, 1200), new PixelSize(400, 400), Token);

        Assert.Equal(new PixelSize(400, 300), detail!.Before.PixelSize);
        Assert.Equal(new PixelSize(400, 300), detail.After.PixelSize);
    }

    [AvaloniaFact]
    public async Task DisplayDecode_FitsTargetPixelsWithoutUpscaling()
    {
        var path = WriteJpeg("display.jpg", 1600, 1200, OrientationType.TopLeft);
        using var source = await CompareImageSource.OpenAsync(path, path, Token);

        var (before, after) = await source.DecodeDisplayAsync(new PixelSize(600, 600), Token);
        using (before)
        using (after)
        {
            Assert.Equal(new PixelSize(600, 450), before.PixelSize);
            Assert.Equal(new PixelSize(600, 450), after.PixelSize);
        }

        // libjpeg 只能按 n/8 缩放：694×520 需要 1/2 档再缩小，不能落到更小的 3/8 档
        var (sharp, sharpAfter) = await source.DecodeDisplayAsync(new PixelSize(694, 520), Token);
        using (sharp)
        using (sharpAfter)
        {
            Assert.Equal(new PixelSize(693, 520), sharp.PixelSize);
        }

        var (full, fullAfter) = await source.DecodeDisplayAsync(new PixelSize(4000, 4000), Token);
        using (full)
        using (fullAfter)
        {
            Assert.Equal(new PixelSize(1600, 1200), full.PixelSize);
        }
    }

    [AvaloniaFact]
    public async Task ExifOrientation_IsAppliedToSizeAndPixels()
    {
        var path = WriteJpeg("rotated.jpg", 400, 200, OrientationType.RightTop);
        using var source = await CompareImageSource.OpenAsync(path, path, Token);

        Assert.Equal(new PixelSize(200, 400), source.SourceSize);
        var (before, after) = await source.DecodeDisplayAsync(new PixelSize(200, 400), Token);
        using (before)
        using (after)
        {
            Assert.Equal(new PixelSize(200, 400), before.PixelSize);
        }
    }

    [AvaloniaFact]
    public async Task Disposed_ReturnsNoDetail()
    {
        var path = WritePng("gone.png", 256, 256, seed: 6);
        var source = await CompareImageSource.OpenAsync(path, path, Token);

        source.Dispose();

        Assert.Null(await source.GetDetailAsync(new PixelRect(0, 0, 64, 64), new PixelSize(64, 64), Token));
    }

    [Theory]
    [InlineData(1600, 1200, 694, 520, 4)]
    [InlineData(1600, 1200, 600, 450, 3)]
    [InlineData(4032, 3024, 1000, 750, 2)]
    [InlineData(4032, 3024, 100, 75, 1)]
    public void JpegSizeHint_SelectsSmallestEighthNotBelowTarget(int w, int h, int tw, int th, int expectedEighths)
    {
        var hint = CompareImageSource.JpegSizeHint(new PixelSize(w, h), new PixelSize(tw, th));

        Assert.NotNull(hint);
        // 无论按取整还是四舍五入换算档位，都落在同一档
        foreach (var ratio in new[] { 8.0 * hint.Value.Width / w, 8.0 * hint.Value.Height / h })
        {
            Assert.Equal(expectedEighths, (int)Math.Floor(ratio));
            Assert.Equal(expectedEighths, (int)Math.Round(ratio));
        }

        Assert.True(w * expectedEighths / 8 >= tw && h * expectedEighths / 8 >= th);
    }

    [Fact]
    public void JpegSizeHint_NoneWhenFullSizeIsNeeded() =>
        Assert.Null(CompareImageSource.JpegSizeHint(new PixelSize(1600, 1200), new PixelSize(1500, 1200)));

    [Fact]
    public void FitWithin_AndMap_KeepAspectAndPosition()
    {
        Assert.Equal(new PixelSize(400, 300), CompareImageSource.FitWithin(new PixelSize(1600, 1200), new PixelSize(400, 400)));
        Assert.Equal(new PixelSize(100, 50), CompareImageSource.FitWithin(new PixelSize(100, 50), new PixelSize(400, 400)));
        Assert.Equal(new PixelRect(10, 10, 20, 20), CompareImageSource.Map(new PixelRect(10, 10, 20, 20), new PixelSize(100, 100), new PixelSize(100, 100)));
        // 产物宽高各多出一倍时，同一内容位置按比例换算
        Assert.Equal(new PixelRect(20, 30, 40, 60), CompareImageSource.Map(new PixelRect(10, 15, 20, 30), new PixelSize(100, 100), new PixelSize(200, 200)));
    }

    private string WritePng(string name, int width, int height, int seed)
    {
        var path = Path.Combine(_sandbox.InputDirectory, name);
        using var image = new MagickImage($"gradient:#{seed * 30 % 256:X2}2040-#F0{seed * 50 % 256:X2}10", (uint)width, (uint)height);
        new Drawables().FillColor(MagickColors.White).Rectangle(width * 0.3, height * 0.3, width * 0.35, height * 0.6).Draw(image);
        image.Write(path, MagickFormat.Png24);
        return path;
    }

    private string WriteJpeg(string name, int width, int height, OrientationType orientation)
    {
        var path = Path.Combine(_sandbox.InputDirectory, name);
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)width, (uint)height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.SetProfile(exif);
        image.Orientation = orientation;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static uint SourcePixel(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        var bytes = image.GetPixelsUnsafe().ToByteArray(x, y, 1, 1, PixelMapping.BGRA)!;
        return BitConverter.ToUInt32(bytes);
    }

    private static uint BitmapPixel(Bitmap bitmap, int x, int y)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
