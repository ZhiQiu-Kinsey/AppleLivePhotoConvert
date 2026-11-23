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
    public void Parse_NegativeItemLengthAfterVideo_IsTreatedAsZero()
    {
        const string xmp = """
                           <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                           <rdf:Description rdf:about="" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/">
                           <Container:Directory><rdf:Seq>
                           <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/></rdf:li>
                           <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="MotionPhoto" Item:Mime="video/mp4" Item:Length="5000" Item:Padding="-3"/></rdf:li>
                           <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="Depth" Item:Mime="image/jpeg" Item:Length="-7000"/></rdf:li>
                           </rdf:Seq></Container:Directory>
                           </rdf:Description></rdf:RDF></x:xmpmeta>
                           """;

        Assert.Equal((5000L, 0L), MotionPhotoXmp.Parse(xmp)?.VideoExtent);
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

    [Fact]
    public void RemoveAndroidDeclarations_DropsGainMapAndOppoDeclarations()
    {
        // 一加 / OPPO 的 XMP：Container 目录声明增益图、hdrgm 版本与厂商私有字段
        const string xmp = """
                           <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                           <rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/"
                               xmlns:OpCamera="http://ns.oplus.com/photos/1.0/camera/"
                               xmlns:Container="http://ns.google.com/photos/1.0/container/"
                               xmlns:Item="http://ns.google.com/photos/1.0/container/item/"
                               hdrgm:Version="1.0" OpCamera:OLivePhotoVersion="2" OpCamera:VideoLength="5851656">
                           <Container:Directory><rdf:Seq>
                           <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/></rdf:li>
                           <rdf:li rdf:parseType="Resource"><Container:Item Item:Semantic="GainMap" Item:Mime="image/jpeg" Item:Length="1149275"/></rdf:li>
                           </rdf:Seq></Container:Directory>
                           </rdf:Description></rdf:RDF></x:xmpmeta>
                           """;

        var stripped = MotionPhotoXmp.RemoveAndroidDeclarations(xmp);

        Assert.NotNull(stripped);
        // 声明全部消失：否则 iOS 会以为封面带增益图，按 HDR 渲染视频帧而发灰
        Assert.DoesNotContain("GainMap", stripped);
        Assert.DoesNotContain("hdrgm", stripped);
        Assert.DoesNotContain("OpCamera", stripped);
        Assert.DoesNotContain("Container", stripped);
        Assert.Null(MotionPhotoXmp.Parse(stripped));
    }

    [Fact]
    public void RemoveAndroidDeclarations_KeepsUnrelatedXmp()
    {
        const string xmp = """
                           <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                           <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"
                               xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" GCamera:MotionPhoto="1">
                           <dc:creator><rdf:Seq><rdf:li>Someone</rdf:li></rdf:Seq></dc:creator>
                           </rdf:Description></rdf:RDF></x:xmpmeta>
                           """;

        var stripped = MotionPhotoXmp.RemoveAndroidDeclarations(xmp);

        Assert.NotNull(stripped);
        Assert.Contains("Someone", stripped);
        Assert.DoesNotContain("GCamera", stripped);
    }

    [Fact]
    public void RemoveAndroidDeclarations_NoXmp_ReturnsNull()
    {
        Assert.Null(MotionPhotoXmp.RemoveAndroidDeclarations(null));
        Assert.Null(MotionPhotoXmp.RemoveAndroidDeclarations("   "));
    }
}
