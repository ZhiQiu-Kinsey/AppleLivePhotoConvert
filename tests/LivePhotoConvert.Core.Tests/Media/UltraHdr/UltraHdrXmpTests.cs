using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.UltraHdr;

namespace LivePhotoConvert.Core.Tests.Media.UltraHdr;

public class UltraHdrXmpTests
{
    private const string AppleXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:HDRGainMap="http://ns.apple.com/HDRGainMap/1.0/"
          xmp:CreateDate="2024-10-06T18:13:19" HDRGainMap:HDRGainMapVersion="65536">
          <xmp:Label>保留</xmp:Label>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        """;

    [Fact]
    public void ApplyPrimary_KeepsOtherNamespacesAndDeclaresGainMap()
    {
        var xmp = UltraHdrXmp.ApplyPrimary(AppleXmp, 12345);

        Assert.Contains("xmp:CreateDate=\"2024-10-06T18:13:19\"", xmp, StringComparison.Ordinal);
        Assert.Contains("HDRGainMap:HDRGainMapVersion=\"65536\"", xmp, StringComparison.Ordinal);
        Assert.Contains("<xmp:Label>保留</xmp:Label>", xmp, StringComparison.Ordinal);
        Assert.Contains("hdrgm:Version=\"1.0\"", xmp, StringComparison.Ordinal);
        var parsed = MotionPhotoXmp.Parse(xmp);
        Assert.NotNull(parsed);
        Assert.Equal(["Primary", "GainMap"], parsed.Items.Select(item => item.Semantic));
        Assert.Equal(12345, parsed.Items[1].Length);
        Assert.Null(parsed.VideoExtent);
    }

    [Fact]
    public void ApplyPrimary_ReplacesStaleGainMapAndKeepsVideoAfterIt()
    {
        var existing = MotionPhotoXmp.Apply(UltraHdrXmp.ApplyPrimary(null, 100), videoLength: 5000, presentationTimestampUs: 0);

        var xmp = UltraHdrXmp.ApplyPrimary(existing, 200);

        var parsed = MotionPhotoXmp.Parse(xmp)!;
        Assert.Equal(["Primary", "GainMap", "MotionPhoto"], parsed.Items.Select(item => item.Semantic));
        Assert.Equal(200, parsed.Items[1].Length);
        Assert.Equal((5000L, 0L), parsed.VideoExtent);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xmp, "hdrgm:Version"));
    }

    [Fact]
    public void MotionPhotoApply_AfterUltraHdr_ListsPrimaryGainMapAndVideoInFileOrder()
    {
        var xmp = MotionPhotoXmp.Apply(UltraHdrXmp.ApplyPrimary(AppleXmp, 300), videoLength: 4000, presentationTimestampUs: 1_000);

        var parsed = MotionPhotoXmp.Parse(xmp)!;
        Assert.Equal(["Primary", "GainMap", "MotionPhoto"], parsed.Items.Select(item => item.Semantic));
        Assert.Equal((4000L, 0L), parsed.VideoExtent);
        Assert.Contains("hdrgm:Version=\"1.0\"", xmp, StringComparison.Ordinal);
        Assert.Contains("HDRGainMap:HDRGainMapVersion", xmp, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPrimary_WithoutExistingXmp_CreatesMinimalPacket()
    {
        var parsed = MotionPhotoXmp.Parse(UltraHdrXmp.ApplyPrimary(null, 42));

        Assert.True(parsed?.HasGainMap);
    }
}
