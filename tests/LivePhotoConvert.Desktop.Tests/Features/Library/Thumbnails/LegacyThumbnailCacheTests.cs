using LivePhotoConvert.Core.Media.Thumbnails;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;

namespace LivePhotoConvert.Desktop.Tests.Features.Library.Thumbnails;

public class LegacyThumbnailCacheTests
{
    [Fact]
    public void TryDelete_RemovesOldFlatCache_AndToleratesMissingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lpc_legacy_{Guid.NewGuid():N}");
        var legacy = Path.Combine(root, "thumbs");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "0123.jpg"), "x");
        File.WriteAllText(Path.Combine(legacy, "0123.meta"), "1\n1");

        try
        {
            Assert.True(LegacyThumbnailCache.TryDelete(legacy));
            Assert.False(Directory.Exists(legacy));
            Assert.True(LegacyThumbnailCache.TryDelete(legacy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyDirectory_IsSeparateFromTieredCache()
    {
        Assert.NotEqual(Path.GetFullPath(ThumbnailDiskCache.DefaultRoot), Path.GetFullPath(LegacyThumbnailCache.DefaultDirectory));
        Assert.Equal(Path.GetDirectoryName(ThumbnailDiskCache.DefaultRoot), Path.GetDirectoryName(LegacyThumbnailCache.DefaultDirectory));
    }
}
