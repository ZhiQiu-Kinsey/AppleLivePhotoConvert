using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class HeifItemsTests
{
    [Fact]
    public void ContainsItemType_FindsTypesInVersion2And3Entries()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("a.heic", SyntheticMedia.HeifWithItems("hvc1", "grid", "tmap"));

        Assert.True(HeifItems.HasToneMapItem(path));
        Assert.True(HeifItems.ContainsItemType(path, "grid"));
        Assert.True(HeifItems.ContainsItemType(path, "hvc1"));
        Assert.False(HeifItems.ContainsItemType(path, "Exif"));
    }

    [Fact]
    public void HasToneMapItem_OrdinaryOrNonHeifFiles_IsFalse()
    {
        using var temp = new TempDirectory();

        Assert.False(HeifItems.HasToneMapItem(temp.CreateFile("a.heic", SyntheticMedia.HeifWithItems("hvc1", "grid"))));
        Assert.False(HeifItems.HasToneMapItem(temp.CreateFile("b.heic", SyntheticMedia.Heic())));
        Assert.False(HeifItems.HasToneMapItem(temp.CreateFile("c.jpg", SyntheticMedia.Jpeg())));
        Assert.False(HeifItems.HasToneMapItem(temp.CreateFile("d.heic", [])));
        Assert.False(HeifItems.HasToneMapItem(temp.Combine("missing.heic")));
    }

    [Fact]
    public void HasToneMapItem_TruncatedMeta_IsFalse()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticMedia.HeifWithItems("tmap");

        Assert.False(HeifItems.HasToneMapItem(temp.CreateFile("a.heic", bytes[..40])));
    }

    [Fact]
    public void AppleSample_HasGainMapAuxiliaryButNoToneMap()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "Media", "UltraHdr", "Fixtures", "hdr-sample.heic");

        Assert.False(HeifItems.HasToneMapItem(sample));
        Assert.True(HeifItems.ContainsItemType(sample, "hvc1"));
    }
}
