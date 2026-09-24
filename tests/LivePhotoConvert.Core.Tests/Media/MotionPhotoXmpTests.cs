using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class MotionPhotoXmpTests
{
    [Fact]
    public void Parse_AttributeForm_ReadsMicroVideoOffset()
    {
        const string xmp = """
                           <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                           <rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" GCamera:MicroVideo="1" GCamera:MicroVideoOffset="4096"/>
                           </rdf:RDF></x:xmpmeta>
                           """;

        var parsed = MotionPhotoXmp.Parse(xmp);

        Assert.NotNull(parsed);
        Assert.Equal((4096L, 0L), parsed.VideoExtent);
    }

    [Fact]
    public void Parse_ElementForm_ReadsMicroVideoOffset()
    {
        const string xmp = """
                           <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                           <rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/">
                           <GCamera:MicroVideoOffset>2048</GCamera:MicroVideoOffset>
                           </rdf:Description></rdf:RDF></x:xmpmeta>
                           """;

        Assert.Equal((2048L, 0L), MotionPhotoXmp.Parse(xmp)?.VideoExtent);
    }

    [Fact]
    public void Parse_ContainerWithGainMapBeforeVideo_UsesVideoItemLength()
    {
        var bytes = SyntheticMedia.MotionPhotoWithGainMap(new byte[777], SyntheticMedia.Mp4(5000));
        using var stream = new MemoryStream(bytes);

        var parsed = MotionPhotoXmp.Parse(MotionPhotoLayout.ReadJpegXmp(stream));

        Assert.NotNull(parsed);
        Assert.True(parsed.HasGainMap);
        Assert.Equal((5000L, 0L), parsed.VideoExtent);
    }

    [Fact]
    public void Parse_GainMapOnly_HasNoVideo()
    {
        using var stream = new MemoryStream(SyntheticMedia.UltraHdrStill(new byte[777]));

        var parsed = MotionPhotoXmp.Parse(MotionPhotoLayout.ReadJpegXmp(stream));

        Assert.NotNull(parsed);
        Assert.True(parsed.HasGainMap);
        Assert.Null(parsed.VideoExtent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<not xml")]
    [InlineData("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>")]
    public void Parse_InvalidOrUnrelated_ReturnsNull(string? xmp) => Assert.Null(MotionPhotoXmp.Parse(xmp));

    [Fact]
    public void Apply_PreservesExistingNamespacesAndGainMapItem()
    {
        using var stream = new MemoryStream(SyntheticMedia.UltraHdrStill(new byte[777]));
        var existing = MotionPhotoLayout.ReadJpegXmp(stream);

        var updated = MotionPhotoXmp.Apply(existing, 9000, 1_200_000);
        var parsed = MotionPhotoXmp.Parse(updated);

        Assert.NotNull(parsed);
        Assert.Equal(["Primary", "GainMap", "MotionPhoto"], parsed.Items.Select(item => item.Semantic));
        Assert.Equal((9000L, 0L), parsed.VideoExtent);
        Assert.Equal(9000, parsed.MicroVideoOffset);
        Assert.Equal(1_200_000, parsed.PresentationTimestampUs);
    }

    [Fact]
    public void Apply_ReplacesStaleVideoDeclaration()
    {
        var first = MotionPhotoXmp.Apply(null, 1000, 0);

        var parsed = MotionPhotoXmp.Parse(MotionPhotoXmp.Apply(first, 2000, 0));

        Assert.NotNull(parsed);
        Assert.Single(parsed.Items, item => item.IsMotionPhoto);
        Assert.Equal((2000L, 0L), parsed.VideoExtent);
    }

    [Fact]
    public void Apply_KeepsUnrelatedProperties()
    {
        const string existing = """
                                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                                <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="5"/>
                                </rdf:RDF></x:xmpmeta>
                                """;

        var updated = MotionPhotoXmp.Apply(existing, 1000, 0);

        Assert.Contains("Rating=\"5\"", updated);
    }

    [Fact]
    public void Remove_DropsVideoButKeepsGainMap()
    {
        using var stream = new MemoryStream(SyntheticMedia.MotionPhotoWithGainMap(new byte[777]));

        var parsed = MotionPhotoXmp.Parse(MotionPhotoXmp.Remove(MotionPhotoLayout.ReadJpegXmp(stream)));

        Assert.NotNull(parsed);
        Assert.Null(parsed.VideoExtent);
        Assert.True(parsed.HasGainMap);
        Assert.DoesNotContain(parsed.Items, item => item.IsMotionPhoto);
    }

    [Fact]
    public void Remove_PlainMotionPhoto_LeavesNoDeclaration()
    {
        var stripped = MotionPhotoXmp.Remove(MotionPhotoXmp.Apply(null, 1000, 0));

        Assert.NotNull(stripped);
        Assert.Null(MotionPhotoXmp.Parse(stripped));
        Assert.DoesNotContain("MicroVideo", stripped);
    }
}
