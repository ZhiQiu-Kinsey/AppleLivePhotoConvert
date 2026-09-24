using ImageMagick;
using LivePhotoConvert.Core.Media.UltraHdr;

namespace LivePhotoConvert.Core.Tests.Media.UltraHdr;

public class AppleGainMapConverterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>调研原型 apple2iso.py 的 build_lut(H) 输出（H 为 hdr-sample 样片的余量 7.3717…）。</summary>
    private const string PrototypeLut =
        "00010102030304050606070808090a0a0b0c0c0d0d0e0f0f101111121314141516171718191a1b1c1c1d1e1f202122232425262728292a2b2c2d2e2f3031"
        + "32333435363738393a3b3d3e3f4041424344454748494a4b4c4d4f5051525354565758595a5b5d5e5f6061626465666768696b6c6d6e6f70727374757677"
        + "797a7b7c7d7e7f81828384858688898a8b8c8d8e8f91929394959697989a9b9c9d9e9fa0a1a2a4a5a6a7a8a9aaabacadaeafb1b2b3b4b5b6b7b8b9babbbc"
        + "bdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e7e8e9eaebecedeeeff0f1f2f3f3f4f5f6f7f8"
        + "f9fafbfbfcfdfeff";

    [Fact]
    public void BuildLookupTable_MatchesPrototype()
    {
        Assert.Equal(PrototypeLut, Convert.ToHexStringLower(AppleGainMapConverter.BuildLookupTable(7.371720837265519)));
    }

    [Fact]
    public void BuildLookupTable_QuantizationErrorWithinSixThousandthsOfAStop()
    {
        foreach (var headroom in (double[])[1.5, 3.4822022531844965, 7.371720837265519, 8])
        {
            var table = AppleGainMapConverter.BuildLookupTable(headroom);
            var stops = Math.Log2(headroom);
            for (var v = 0; v < 256; v++)
            {
                var apple = Math.Log2(1 + (headroom - 1) * AppleGainMapConverter.Rec709InverseOetf(v / 255.0));
                var iso = table[v] / 255.0 * stops;
                Assert.True(Math.Abs(apple - iso) <= 0.5 / 255 * stops + 1e-12, $"H={headroom} v={v}");
                Assert.True(v == 0 || table[v] >= table[v - 1], "查找表必须单调");
            }

            Assert.Equal(0, table[0]);
            Assert.Equal(255, table[255]);
        }
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0405, 0.009)]                     // 线性段：v / 4.5
    [InlineData(0.5, 0.25958940050628576)]          // 幂函数段：((v + 0.099) / 1.099)^(1/0.45)
    [InlineData(1.0, 1.0)]
    public void Rec709InverseOetf_UsesRec709NotSrgb(double value, double expected)
    {
        Assert.Equal(expected, AppleGainMapConverter.Rec709InverseOetf(value), 12);
    }

    [Fact]
    public void BuildLookupTable_RejectsHeadroomWithoutHdrEffect()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppleGainMapConverter.BuildLookupTable(1.0));
    }

    [Fact]
    public async Task ConvertAsync_WritesGrayscaleJpegRemappedThroughTable()
    {
        using var temp = new TempDirectory();
        var source = temp.Combine("apple.png");
        var ramp = new byte[256 * 8];
        for (var i = 0; i < ramp.Length; i++)
        {
            ramp[i] = (byte)(i % 256);
        }

        using (var image = new MagickImage(ramp, new MagickReadSettings { Format = MagickFormat.Gray, Width = 256, Height = 8, Depth = 8 }))
        {
            image.Write(source, MagickFormat.Png);
        }

        var output = temp.Combine("iso.jpg");
        await AppleGainMapConverter.ConvertAsync(source, output, 7.371720837265519, Token);

        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, result.Format);
        Assert.Equal((256u, 8u), (result.Width, result.Height));
        Assert.Equal(1, JpegComponentCount(await File.ReadAllBytesAsync(output, Token)));
        Assert.Null(result.GetExifProfile());
        var table = AppleGainMapConverter.BuildLookupTable(7.371720837265519);
        var pixels = result.ToByteArray(MagickFormat.Gray);
        for (var x = 0; x < 256; x++)
        {
            // JPEG 质量 90 的块效应允许少量偏差
            Assert.InRange(pixels[4 * 256 + x] - table[x], -4, 4);
        }
    }

    /// <summary>SOF 段中的分量数：灰度 JPEG 为 1。</summary>
    private static int JpegComponentCount(byte[] jpeg)
    {
        var position = 2;
        while (position + 4 < jpeg.Length && jpeg[position] == 0xFF)
        {
            var marker = jpeg[position + 1];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return jpeg[position + 9];
            }

            position += 2 + (jpeg[position + 2] << 8 | jpeg[position + 3]);
        }

        return -1;
    }
}
