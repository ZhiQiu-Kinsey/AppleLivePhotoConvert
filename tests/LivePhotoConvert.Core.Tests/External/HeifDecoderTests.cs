using ImageMagick;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

public class HeifDecoderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Media", "UltraHdr", "Fixtures", "hdr-sample.heic");

    [Theory]
    [InlineData(4032u, 3024u, 2016u, 1512u)]
    [InlineData(1512u, 850u, 756u, 425u)]
    [InlineData(1512u, 850u, 756u, 426u)] // 奇数边长的舍入差 1 像素，不重采样
    [InlineData(4032u, 3024u, 1008u, 756u)]
    public void UnifiedGainMapSize_ProportionalSizes_NeedNoResize(uint pw, uint ph, uint gw, uint gh)
    {
        Assert.Null(HeifDecoder.UnifiedGainMapSize(pw, ph, gw, gh));
    }

    [Theory]
    [InlineData(4032u, 3024u, 2016u, 1536u, 2016u, 1512u)] // clap 裁剪后比例略有偏差
    [InlineData(100u, 75u, 300u, 225u, 100u, 75u)]         // 增益图比主图还大
    [InlineData(1001u, 751u, 500u, 380u, 501u, 376u)]
    public void UnifiedGainMapSize_SlightlyDifferentAspect_ResizesToIntegerFraction(uint pw, uint ph, uint gw, uint gh, uint ew, uint eh)
    {
        Assert.Equal((ew, eh), HeifDecoder.UnifiedGainMapSize(pw, ph, gw, gh));
    }

    [Theory]
    [InlineData(4032u, 3024u, 1512u, 2016u)] // 方向不一致
    [InlineData(4032u, 3024u, 2016u, 2016u)]
    [InlineData(0u, 3024u, 2016u, 1512u)]
    public void UnifiedGainMapSize_IncompatibleShapes_Throw(uint pw, uint ph, uint gw, uint gh)
    {
        Assert.Throws<InvalidDataException>(() => HeifDecoder.UnifiedGainMapSize(pw, ph, gw, gh));
    }

    [Fact]
    public async Task DecodeAsync_AppleSample_ExportsPrimaryAndGainMapWithMatchingOrientation()
    {
        var decoder = ExternalTools.RequireHeifDecoder();
        using var temp = new TempDirectory();

        var images = await decoder.DecodeAsync(SamplePath, temp.Combine("decoded"), Token);

        Assert.NotNull(images);
        var primary = new MagickImageInfo(images.PrimaryPath);
        var gainMap = new MagickImageInfo(images.GainMapPath);
        Assert.Equal((1512u, 850u), (primary.Width, primary.Height));
        Assert.Equal((756u, 425u), (gainMap.Width, gainMap.Height));
        Assert.Equal(MagickFormat.Jpeg, primary.Format);
    }

    [Fact]
    public async Task DecodeAsync_HeicWithoutGainMap_ReturnsNull()
    {
        var decoder = ExternalTools.RequireHeifDecoder();
        var heifEnc = ExternalTools.RequireHeifEnc();
        using var temp = new TempDirectory();
        var jpeg = temp.Combine("sdr.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 64, 48))
        {
            image.Write(jpeg, MagickFormat.Jpeg);
        }

        var heic = temp.Combine("sdr.heic");
        Assert.True((await ProcessRunner.RunAsync(heifEnc, ["-q", "80", jpeg, "-o", heic], Token)).Success);

        Assert.Null(await decoder.DecodeAsync(heic, temp.Combine("decoded"), Token));
    }

    [Fact]
    public async Task DecodeAsync_CorruptInput_ThrowsInsteadOfReturningNull()
    {
        var decoder = ExternalTools.RequireHeifDecoder();
        using var temp = new TempDirectory();
        var broken = temp.CreateFile("broken.heic", SyntheticMedia.Heic());

        await Assert.ThrowsAnyAsync<Exception>(() => decoder.DecodeAsync(broken, temp.Combine("decoded"), Token));
    }

    [Fact]
    public void Find_PrefersHeifDecNextToHeifEnc()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "需要可执行的 shell 脚本模拟 heif-dec。");
        using var temp = new TempDirectory();
        var heifEnc = temp.CreateFile("heif-enc");
        var heifDec = temp.CreateFile("heif-dec", "#!/bin/sh\nexit 0\n"u8.ToArray());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(heifDec, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(heifDec, HeifDecoder.Find(heifEncPath: heifEnc));
    }
}
