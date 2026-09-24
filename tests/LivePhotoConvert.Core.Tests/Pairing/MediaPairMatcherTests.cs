using LivePhotoConvert.Core.Pairing;

namespace LivePhotoConvert.Core.Tests.Pairing;

public class MediaPairMatcherTests
{
    private static readonly string Album = Path.Combine(Path.GetTempPath(), "album");

    private static string At(params string[] parts) => Path.Combine([Album, .. parts]);

    [Fact]
    public void Match_SameStemInSameDirectory_PairsPhotoWithVideo()
    {
        var result = MediaPairMatcher.Match([At("IMG_0001.heic"), At("IMG_0001.mov")]);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(At("IMG_0001.heic"), pair.PhotoPath);
        Assert.Equal(At("IMG_0001.mov"), pair.VideoPath);
    }

    [Fact]
    public void Match_SameStemInDifferentDirectories_DoesNotCrossPair()
    {
        var result = MediaPairMatcher.Match([At("100APPLE", "IMG_0001.heic"), At("101APPLE", "IMG_0001.mov")]);

        Assert.Empty(result.Pairs);
        Assert.Single(result.PhotosWithoutVideo);
        Assert.Single(result.VideosWithoutPhoto);
    }

    [Fact]
    public void Match_MultipleFormats_ListsCandidatesByPriority()
    {
        var result = MediaPairMatcher.Match([At("IMG_0001.jpg"), At("IMG_0001.mp4"), At("IMG_0001.heic"), At("IMG_0001.mov")]);

        Assert.Equal(
            [(At("IMG_0001.heic"), At("IMG_0001.mov")), (At("IMG_0001.heic"), At("IMG_0001.mp4")), (At("IMG_0001.jpg"), At("IMG_0001.mov")), (At("IMG_0001.jpg"), At("IMG_0001.mp4"))],
            result.Pairs.Select(pair => (pair.PhotoPath, pair.VideoPath)));
    }

    [Fact]
    public void Match_ExtensionCase_DoesNotAffectPairing()
    {
        Assert.Single(MediaPairMatcher.Match([At("IMG_0001.HEIC"), At("IMG_0001.MOV")]).Pairs);
    }

    [Fact]
    public void Match_ReportsUnmatchedAndIgnoresUnrelatedFiles()
    {
        var result = MediaPairMatcher.Match([At("IMG_0001.heic"), At("IMG_0002.mov"), At("notes.txt"), At("IMG_0003.jpg"), At("IMG_0003.mov")]);

        Assert.Single(result.Pairs);
        Assert.Equal([At("IMG_0001.heic")], result.PhotosWithoutVideo);
        Assert.Equal([At("IMG_0002.mov")], result.VideosWithoutPhoto);
    }

    [Fact]
    public void Match_ContentIdentifier_PairsRenamedFilesAndConsumesOtherFormats()
    {
        var identifiers = new Dictionary<string, string>
        {
            [At("IMG_0011-1.heic")] = "UUID-A",
            [At("IMG_0011.jpg")] = "UUID-A",
            [At("IMG_0011.mov")] = "UUID-A"
        };

        var result = MediaPairMatcher.Match([At("IMG_0011-1.heic"), At("IMG_0011.jpg"), At("IMG_0011.mov")], identifiers);

        var pair = Assert.Single(result.Pairs);
        Assert.True(pair.IsContentIdentifierMatched);
        Assert.Equal(At("IMG_0011-1.heic"), pair.PhotoPath);
        Assert.Empty(result.PhotosWithoutVideo);
    }

    [Fact]
    public void Match_EmptyInput_ProducesEmptyResult()
    {
        var result = MediaPairMatcher.Match([]);

        Assert.Empty(result.Pairs);
        Assert.Empty(result.PhotosWithoutVideo);
        Assert.Empty(result.VideosWithoutPhoto);
    }

    [Fact]
    public void PathComparer_IgnoresContentIdentifierFlagAndNormalizesPaths()
    {
        var comparer = MediaPairPathEqualityComparer.Instance;
        var left = new MediaPair(At("IMG_1.heic"), At("IMG_1.mov"));
        var right = new MediaPair(At(".", "IMG_1.heic"), At("IMG_1.mov"), IsContentIdentifierMatched: true);

        Assert.True(comparer.Equals(left, right));
        Assert.Equal(comparer.GetHashCode(left), comparer.GetHashCode(right));
        Assert.False(comparer.Equals(left, null));
    }
}
