using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

public class ThumbnailKeyTests
{
    private static readonly DateTime Modified = new(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly string PhotoPath = Path.Combine(Path.GetTempPath(), "album", "IMG_0001.JPG");

    [Fact]
    public void Create_IsDeterministicAndSplitsByPrefix()
    {
        var a = ThumbnailKey.Create(PhotoPath, 1234, Modified, 512);
        var b = ThumbnailKey.Create(PhotoPath, 1234, Modified, 512);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", a.Hash);
        Assert.Equal(Path.Combine(a.Hash[..2], a.Hash + ".jpg"), a.RelativePath);
        Assert.Equal(512, a.Tier);
    }

    [Fact]
    public void Create_ChangesWithModifiedTimeLengthAndTier()
    {
        var baseline = ThumbnailKey.Create(PhotoPath, 1234, Modified, 512);

        Assert.NotEqual(baseline, ThumbnailKey.Create(PhotoPath, 1234, Modified.AddTicks(1), 512));
        Assert.NotEqual(baseline, ThumbnailKey.Create(PhotoPath, 1235, Modified, 512));
        Assert.NotEqual(baseline, ThumbnailKey.Create(PhotoPath, 1234, Modified, 768));
        Assert.NotEqual(baseline, ThumbnailKey.Create(PhotoPath + "2", 1234, Modified, 512));
    }

    [Fact]
    public void Create_NormalizesEquivalentPathSpellings()
    {
        var directory = Path.GetDirectoryName(PhotoPath)!;
        var roundabout = Path.Combine(directory, "..", "album", "IMG_0001.JPG");

        Assert.Equal(
            ThumbnailKey.Create(PhotoPath, 1, Modified, 256),
            ThumbnailKey.Create(roundabout, 1, Modified, 256));
    }

    [Fact]
    public void Create_TreatsLocalAndUtcTimestampOfSameInstantAlike() =>
        Assert.Equal(
            ThumbnailKey.Create(PhotoPath, 1, Modified, 256),
            ThumbnailKey.Create(PhotoPath, 1, Modified.ToLocalTime(), 256));

    [Fact]
    public void Create_CaseSensitivityFollowsPlatformFileSystem()
    {
        var upper = ThumbnailKey.Create(PhotoPath, 1, Modified, 256);
        var lower = ThumbnailKey.Create(PhotoPath.ToLowerInvariant(), 1, Modified, 256);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(upper, lower);
        }
        else
        {
            Assert.NotEqual(upper, lower);
        }
    }
}
