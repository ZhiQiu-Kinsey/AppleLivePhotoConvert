using ImageMagick;
using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class ThumbnailGeneratorTests
{
    private static ThumbnailGenerator CreateGenerator(
        TempDirectory dir,
        IThumbnailSource? platform = null,
        ThumbnailDecodeLimits? limits = null,
        ThumbnailDecoder? decoder = null) =>
        new(new ThumbnailDiskCache(dir.Combine("cache"), 100_000_000), platform, limits ?? new ThumbnailDecodeLimits(), decoder);

    private static string WritePhoto(TempDirectory dir, string name, byte[] content) => dir.CreateFile(Path.Combine("album", name), content);

    [Fact]
    public async Task Orientation6_ProducesPortraitWithTierHeight()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "o6.jpg", ThumbnailTestImages.WithExif(ThumbnailTestImages.Jpeg(4000, 3000, MagickColors.Red), 6));
        var generator = CreateGenerator(dir);

        var result = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 384), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ThumbnailOrigin.Decoded, result.Origin);
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal(384u, image.Height);
        Assert.Equal(288u, image.Width);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.Null(image.GetExifProfile());
        // 4:4:4 不做色度抽样
        Assert.Equal("1x1,1x1,1x1", image.GetAttribute("jpeg:sampling-factor"));
        Assert.True(File.Exists(result.CachePath));
    }

    [Fact]
    public async Task SmallEmbeddedThumbnail_IsIgnored()
    {
        using var dir = new TempDirectory();
        var main = ThumbnailTestImages.Jpeg(1600, 1200, MagickColors.Red);
        var embedded = ThumbnailTestImages.Jpeg(160, 120, MagickColors.Blue);
        var photo = WritePhoto(dir, "small-exif.jpg", ThumbnailTestImages.WithExif(main, 1, embedded));

        var result = await CreateGenerator(dir).GetAsync(ThumbnailRequest.ForFile(photo, 256), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ThumbnailOrigin.Decoded, result.Origin);
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal((341u, 256u), (image.Width, image.Height));
        var (r, _, b) = ThumbnailTestImages.PixelAt(image, 100, 100);
        Assert.True(r > 200 && b < 50, "应来自主图（红）而不是内嵌缩略图（蓝）");
    }

    [Fact]
    public async Task LargeEmbeddedThumbnail_IsUsedAndOriented()
    {
        using var dir = new TempDirectory();
        var main = ThumbnailTestImages.Jpeg(4000, 3000, MagickColors.Red);
        var embedded = ThumbnailTestImages.Jpeg(640, 480, MagickColors.Blue);
        var photo = WritePhoto(dir, "big-exif.jpg", ThumbnailTestImages.WithExif(main, 6, embedded));
        var decodes = 0;

        var generator = CreateGenerator(dir, decoder: (path, settings) =>
        {
            Interlocked.Increment(ref decodes);
            return new MagickImage(path, settings);
        });
        var result = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 384), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ThumbnailOrigin.Embedded, result.Origin);
        Assert.Equal(0, decodes);
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal((288u, 384u), (image.Width, image.Height));
        var (r, _, b) = ThumbnailTestImages.PixelAt(image, 100, 100);
        Assert.True(b > 200 && r < 50);
    }

    [Fact]
    public async Task SmallSource_IsNotUpscaled()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "tiny.jpg", ThumbnailTestImages.Jpeg(200, 150, MagickColors.Green));

        var result = await CreateGenerator(dir).GetAsync(ThumbnailRequest.ForFile(photo, 1024), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal((200u, 150u), (image.Width, image.Height));
    }

    [Fact]
    public async Task AdobeRgbSource_IsConvertedToSrgbWithoutProfile()
    {
        using var dir = new TempDirectory();
        var green = new MagickColor(60, 180, 60);
        var tagged = WritePhoto(dir, "adobe.jpg", ThumbnailTestImages.Jpeg(400, 300, green, ColorProfiles.AdobeRGB1998));
        var untagged = WritePhoto(dir, "plain.jpg", ThumbnailTestImages.Jpeg(400, 300, green));
        var generator = CreateGenerator(dir);

        var adobe = await generator.GetAsync(ThumbnailRequest.ForFile(tagged, 256), TestContext.Current.CancellationToken);
        var plain = await generator.GetAsync(ThumbnailRequest.ForFile(untagged, 256), TestContext.Current.CancellationToken);

        Assert.NotNull(adobe);
        Assert.NotNull(plain);
        using var adobeImage = ThumbnailTestImages.Read(adobe);
        using var plainImage = ThumbnailTestImages.Read(plain);
        Assert.Null(adobeImage.GetColorProfile());
        Assert.Null(plainImage.GetColorProfile());

        // 无 ICC 按 sRGB 原样保留；AdobeRGB 的饱和绿超出 sRGB 色域，红分量被压到接近 0
        var (pr, pg, pb) = ThumbnailTestImages.PixelAt(plainImage, 50, 50);
        Assert.InRange(pr, 55, 65);
        Assert.InRange(pg, 175, 185);
        Assert.InRange(pb, 55, 65);
        var (ar, _, _) = ThumbnailTestImages.PixelAt(adobeImage, 50, 50);
        Assert.True(ar < 30, $"AdobeRGB 未转换到 sRGB：R={ar}");
    }

    [Fact]
    public async Task JpegDecode_PassesSizeHintInStoredOrientation()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "hint.jpg", ThumbnailTestImages.WithExif(ThumbnailTestImages.Jpeg(4000, 3000, MagickColors.Red), 6));
        string? define = null;
        (uint Width, uint Height) decoded = default;
        var generator = CreateGenerator(dir, decoder: (path, settings) =>
        {
            define = settings.GetDefine(MagickFormat.Jpeg, "size");
            var image = new MagickImage(path, settings);
            decoded = (image.Width, image.Height);
            return image;
        });

        var result = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 512), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(define);
        var parts = define.Split('x').Select(int.Parse).ToArray();
        // 存储方向是横向 4000×3000：提示必须是横向且覆盖 512×384（转正后 384×512 的存储方向尺寸）
        Assert.True(parts[0] > parts[1], define);
        Assert.True(parts[0] >= 512 && parts[1] >= 384, define);
        Assert.True(decoded.Width >= 512 && decoded.Height >= 384, $"{decoded}");
        Assert.True(decoded.Width < 4000, "应走 DCT 缩放而不是完整解码");
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal((384u, 512u), (image.Width, image.Height));
    }

    [Theory]
    [InlineData(8000, 6000, 6, 1024, 2000, 1500, 1024, 768)]
    [InlineData(8000, 6000, 1, 1024, 2000, 1500, 1365, 1024)]
    [InlineData(4000, 3000, 6, 512, 1000, 750, 512, 384)]
    [InlineData(4000, 3000, 1, 256, 500, 375, 341, 256)]
    public void PlanJpegScale_RequestsExactEighthCoveringTarget(
        int width, int height, int orientation, int tier, int hintWidth, int hintHeight, int targetWidth, int targetHeight)
    {
        var plan = ThumbnailGenerator.PlanJpegScale(
            new ThumbnailGenerator.ProbeResult(ThumbnailDecodeKind.Jpeg, width, height, (OrientationType)orientation, null), tier);

        Assert.NotNull(plan);
        Assert.Equal((hintWidth, hintHeight), ((int)plan.Value.Hint.Width, (int)plan.Value.Hint.Height));
        Assert.Equal((targetWidth, targetHeight), (plan.Value.TargetWidth, plan.Value.TargetHeight));
    }

    [Fact]
    public void PlanJpegScale_NoHintWhenTargetNeedsFullResolution() =>
        Assert.Null(ThumbnailGenerator.PlanJpegScale(
            new ThumbnailGenerator.ProbeResult(ThumbnailDecodeKind.Jpeg, 1100, 1000, OrientationType.TopLeft, null), 1024));

    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(3000, 4000)]
    [InlineData(4032, 3024)]
    public async Task JpegDecode_RealDctScalingAlwaysCoversTier(int width, int height)
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, $"{width}x{height}.jpg", ThumbnailTestImages.Jpeg(width, height, MagickColors.Gray));
        var generator = CreateGenerator(dir);

        foreach (var tier in ThumbnailTiers.Heights.ToArray())
        {
            var result = await generator.GetAsync(ThumbnailRequest.ForFile(photo, tier), TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            using var image = ThumbnailTestImages.Read(result);
            Assert.Equal((uint)tier, image.Height);
        }
    }

    [Fact]
    public async Task HeifDecodes_NeverRunConcurrently_JpegAtMostTwo()
    {
        using var dir = new TempDirectory();
        var heics = Enumerable.Range(0, 6).Select(i => WritePhoto(dir, $"h{i}.heic", SyntheticMedia.Heic())).ToArray();
        var jpegs = Enumerable.Range(0, 6).Select(i => WritePhoto(dir, $"j{i}.jpg", SyntheticMedia.Jpeg())).ToArray();
        // 下标 0 为 HEIC，1 为 JPEG
        var active = new int[2];
        var peak = new int[2];

        var generator = CreateGenerator(dir, decoder: (path, _) =>
        {
            var kind = path.EndsWith(".heic", StringComparison.Ordinal) ? 0 : 1;
            InterlockedMax(ref peak[kind], Interlocked.Increment(ref active[kind]));
            Thread.Sleep(40);
            Interlocked.Decrement(ref active[kind]);
            return new MagickImage(MagickColors.Orange, 300, 200);
        });

        var results = await Task.WhenAll(heics.Concat(jpegs).Select(p =>
            generator.GetAsync(ThumbnailRequest.ForFile(p, 256), TestContext.Current.CancellationToken)));

        Assert.All(results, r => Assert.Equal(ThumbnailOrigin.Decoded, r!.Origin));
        Assert.Equal(1, peak[0]);
        Assert.InRange(peak[1], 1, 2);
    }

    [Fact]
    public async Task SecondRequest_HitsCache_ModifiedSourceRegenerates()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "cached.jpg", ThumbnailTestImages.Jpeg(800, 600, MagickColors.Red));
        var decodes = 0;
        var generator = CreateGenerator(dir, decoder: (path, settings) =>
        {
            Interlocked.Increment(ref decodes);
            return new MagickImage(path, settings);
        });

        var first = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 256), TestContext.Current.CancellationToken);
        var second = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 256), TestContext.Current.CancellationToken);
        Assert.Equal(ThumbnailOrigin.Decoded, first!.Origin);
        Assert.Equal(ThumbnailOrigin.Cache, second!.Origin);
        Assert.Null(second.Data);
        using (var cached = ThumbnailTestImages.Read(second))
        {
            Assert.Equal(256u, cached.Height);
        }

        Assert.NotNull(generator.TryGetCached(ThumbnailRequest.ForFile(photo, 256)));
        Assert.Null(generator.TryGetCached(ThumbnailRequest.ForFile(photo, 384)));

        File.SetLastWriteTimeUtc(photo, File.GetLastWriteTimeUtc(photo).AddMinutes(1));
        var third = await generator.GetAsync(ThumbnailRequest.ForFile(photo, 256), TestContext.Current.CancellationToken);
        Assert.Equal(ThumbnailOrigin.Decoded, third!.Origin);
        Assert.Equal(2, decodes);
    }

    [Fact]
    public async Task PlatformThumbnail_UsedWhenLargeEnough()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "p.jpg", ThumbnailTestImages.Jpeg(4000, 3000, MagickColors.Red));
        var platform = new FakePlatformSource(() => new MagickImage(MagickColors.Blue, 1365, 1024));
        var request = ThumbnailRequest.ForFile(photo, 512) with { Header = (4000, 3000, 1) };

        var result = await CreateGenerator(dir, platform).GetAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailOrigin.Platform, result!.Origin);
        Assert.Equal((683, 512), platform.LastBox);
        using var image = ThumbnailTestImages.Read(result);
        Assert.Equal((683u, 512u), (image.Width, image.Height));
    }

    [Fact]
    public async Task PlatformThumbnail_TooSmall_FallsBackToDecode()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "p.jpg", ThumbnailTestImages.Jpeg(4000, 3000, MagickColors.Red));
        // 系统只给出 256px 缓存，远小于 512 档位的 0.9 倍
        var platform = new FakePlatformSource(() => new MagickImage(MagickColors.Blue, 341, 256));

        var result = await CreateGenerator(dir, platform).GetAsync(ThumbnailRequest.ForFile(photo, 512), TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailOrigin.Decoded, result!.Origin);
        Assert.Equal((1024, 512), platform.LastBox);
        Assert.True(platform.ReturnedDisposed);
    }

    [Fact]
    public async Task PlatformThumbnail_WithAlpha_IsFlattenedToOpaque()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "alpha.jpg", ThumbnailTestImages.Jpeg(800, 600, MagickColors.Red));
        var platform = new FakePlatformSource(() => new MagickImage(MagickColors.Transparent, 400, 300));

        var result = await CreateGenerator(dir, platform).GetAsync(
            ThumbnailRequest.ForFile(photo, 256) with { Header = (800, 600, 1) },
            TestContext.Current.CancellationToken);

        Assert.Equal(ThumbnailOrigin.Platform, result!.Origin);
        using var image = ThumbnailTestImages.Read(result);
        Assert.False(image.HasAlpha);
        Assert.Equal(((byte)255, (byte)255, (byte)255), ThumbnailTestImages.PixelAt(image, 10, 10));
    }

    [Fact]
    public async Task CorruptFile_ReturnsNullAndCachesNothing()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "broken.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        var generator = CreateGenerator(dir);

        Assert.Null(await generator.GetAsync(ThumbnailRequest.ForFile(photo, 256), TestContext.Current.CancellationToken));
        Assert.Equal(0, generator.Cache.Trim().BytesAfter);
    }

    [Fact]
    public async Task CanceledRequest_Throws()
    {
        using var dir = new TempDirectory();
        var photo = WritePhoto(dir, "c.jpg", ThumbnailTestImages.Jpeg(200, 150, MagickColors.Red));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateGenerator(dir).GetAsync(ThumbnailRequest.ForFile(photo, 256), cts.Token));
    }

    [Fact]
    public void Request_RejectsNonTierHeight() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailRequest("/a.jpg", 1, DateTime.UtcNow, 500));

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class FakePlatformSource(Func<MagickImage> factory) : IThumbnailSource
    {
        private MagickImage? _returned;

        public (int Width, int Height) LastBox { get; private set; }

        public bool ReturnedDisposed
        {
            get
            {
                try
                {
                    _ = _returned?.Width;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public Task<IMagickImage<byte>?> TryGetAsync(string path, int width, int height, CancellationToken cancellationToken)
        {
            LastBox = (width, height);
            _returned = factory();
            return Task.FromResult<IMagickImage<byte>?>(_returned);
        }
    }
}
