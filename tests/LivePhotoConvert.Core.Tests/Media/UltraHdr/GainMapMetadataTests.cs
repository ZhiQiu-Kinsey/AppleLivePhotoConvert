using LivePhotoConvert.Core.Media.UltraHdr;

namespace LivePhotoConvert.Core.Tests.Media.UltraHdr;

public class GainMapMetadataTests
{
    /// <summary>
    /// libultrahdr 2.0.2（ultrahdr_app -m 0 -i 主图 -g 增益图 -f 配置）对 maxContentBoost = hdrCapacityMax = 7.371721、
    /// minContentBoost = hdrCapacityMin = 1、gamma 1、offset 0、useBaseColorSpace 1 写出的增益图 ISO 21496-1 段内容。
    /// </summary>
    private const string LibUltraHdrIsoPayload =
        "00000000" + "40"
        + "00000000" + "00000001" + "005c395b" + "00200000"
        + "00000000" + "00000001" + "005c395b" + "00200000"
        + "00000001" + "00000001" + "00000000" + "00000001" + "00000000" + "00000001";

    [Fact]
    public void ToIsoPayload_MatchesLibUltraHdrBytes()
    {
        var stops = MathF.Log2(7.371721f);
        var metadata = new GainMapMetadata(0, stops, 1, 0, 0, 0, stops, UseBaseColorSpace: true);

        Assert.Equal(LibUltraHdrIsoPayload, Convert.ToHexStringLower(metadata.ToIsoPayload()));
    }

    [Fact]
    public void FromAppleHeadroom_UsesLog2HeadroomForGainRangeAndCapacity()
    {
        var metadata = GainMapMetadata.FromAppleHeadroom(7.371720837265519);

        Assert.Equal(new GainMapMetadata(0, Math.Log2(7.371720837265519), 1, 0, 0, 0, Math.Log2(7.371720837265519), true), metadata);
        Assert.Equal(LibUltraHdrIsoPayload, Convert.ToHexStringLower(metadata.ToIsoPayload()));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void FromAppleHeadroom_RejectsHeadroomWithoutHdrEffect(double headroom)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GainMapMetadata.FromAppleHeadroom(headroom));
    }

    [Theory]
    [InlineData(0f, 0u, 1u)]
    [InlineData(1f, 1u, 1u)]
    [InlineData(0.5f, 1u, 2u)]
    [InlineData(2.5f, 5u, 2u)]
    [InlineData(0.1f, 13421773u, 134217728u)] // 0.1f 的精确值是 13421773 / 2^27
    public void ToUnsignedFraction_FindsExactOrBestFraction(float value, uint numerator, uint denominator)
    {
        Assert.Equal((numerator, denominator), GainMapMetadata.ToUnsignedFraction(value, uint.MaxValue));
    }

    [Fact]
    public void ToIsoPayload_NegativeOffsetsAreSigned()
    {
        var payload = new GainMapMetadata(-1, 2, 1, -0.5, 0.25, 0, 2, UseBaseColorSpace: false).ToIsoPayload();

        Assert.Equal(0, payload[4]);
        Assert.Equal("ffffffff00000001", Convert.ToHexStringLower(payload.AsSpan(21, 8)));
        Assert.Equal("ffffffff00000002", Convert.ToHexStringLower(payload.AsSpan(45, 8)));
        Assert.Equal("0000000100000004", Convert.ToHexStringLower(payload.AsSpan(53, 8)));
    }

    [Fact]
    public void ToGainMapXmp_DeclaresAllHdrgmParameters()
    {
        var xmp = GainMapMetadata.FromAppleHeadroom(4).ToGainMapXmp();

        foreach (var expected in (string[])["hdrgm:Version=\"1.0\"", "hdrgm:GainMapMin=\"0\"", "hdrgm:GainMapMax=\"2\"", "hdrgm:Gamma=\"1\"",
                     "hdrgm:OffsetSDR=\"0\"", "hdrgm:OffsetHDR=\"0\"", "hdrgm:HDRCapacityMin=\"0\"", "hdrgm:HDRCapacityMax=\"2\"", "hdrgm:BaseRenditionIsHDR=\"False\""])
        {
            Assert.Contains(expected, xmp, StringComparison.Ordinal);
        }
    }
}
