using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;

namespace LivePhotoConvert.Core.Tests.Pairing;

public class PairValidatorTests
{
    private static MediaMetadata Photo(string? id = null, string? time = null) => Build("IMG.heic", id, time, null);

    private static MediaMetadata Video(string? id = null, string? time = null, double? seconds = null) => Build("IMG.mov", id, time, seconds);

    private static MediaMetadata Build(string path, string? id, string? time, double? seconds) => new()
    {
        Path = path,
        ContentIdentifier = id,
        CaptureTime = time is not null && CaptureTime.TryParse(time, out var value) ? value : null,
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null
    };

    [Fact]
    public void MatchingContentIdentifier_Accepts() =>
        Assert.True(PairValidator.Validate(Photo("A"), Video("a", "2020:01:01 00:00:00", 600)).IsAccepted);

    [Fact]
    public void MismatchedContentIdentifier_Rejects() => Assert.False(PairValidator.Validate(Photo("A"), Video("B")).IsAccepted);

    [Theory]
    [InlineData("A", null)]
    [InlineData(null, "A")]
    public void OneSidedContentIdentifier_Rejects(string? photoId, string? videoId) =>
        Assert.False(PairValidator.Validate(Photo(photoId), Video(videoId)).IsAccepted);

    [Fact]
    public void CaptureTimeWithinThreshold_Accepts() =>
        Assert.True(PairValidator.Validate(Photo(time: "2024:05:01 14:03:03"), Video(time: "2024:05:01 14:03:05", seconds: 2.8)).IsAccepted);

    [Fact]
    public void CaptureTimeInDifferentTimeZonesButSameInstant_Accepts() =>
        Assert.True(PairValidator.Validate(Photo(time: "2024:05:01 14:03:03+08:00"), Video(time: "2024:05:01 06:03:03+00:00")).IsAccepted);

    [Fact]
    public void CaptureTimeBeyondThreshold_Rejects() =>
        Assert.False(PairValidator.Validate(Photo(time: "2024:05:01 14:03:03"), Video(time: "2024:05:01 14:05:03", seconds: 2)).IsAccepted);

    [Theory]
    [InlineData("2024:05:01 14:03:03", null)]
    [InlineData(null, "2024:05:01 14:03:03")]
    public void OneSidedCaptureTime_Rejects(string? photoTime, string? videoTime) =>
        Assert.False(PairValidator.Validate(Photo(time: photoTime), Video(time: videoTime)).IsAccepted);

    [Fact]
    public void LongVideo_Rejects() => Assert.False(PairValidator.Validate(Photo(), Video(seconds: 45)).IsAccepted);

    [Fact]
    public void NoSignals_AcceptsByFileName()
    {
        var result = PairValidator.Validate(Photo(), Video());

        Assert.True(result.IsAccepted);
        Assert.NotEmpty(result.Reasons);
    }
}
