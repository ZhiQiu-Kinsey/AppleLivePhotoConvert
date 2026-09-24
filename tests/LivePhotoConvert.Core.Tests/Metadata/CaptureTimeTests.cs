using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Metadata;

public class CaptureTimeTests
{
    [Theory]
    [InlineData("2024:05:01 14:03:03", 14, null)]
    [InlineData("2024:05:01 14:03:03+08:00", 14, 8.0)]
    [InlineData("2024:05:01 06:03:03Z", 6, 0.0)]
    [InlineData("2024:05:01 14:03:03.123-05:30", 14, -5.5)]
    [InlineData("2024-05-01T14:03:03", 14, null)]
    public void TryParse_AcceptsExifToolFormats(string text, int hour, double? offsetHours)
    {
        Assert.True(CaptureTime.TryParse(text, out var value));
        Assert.Equal(hour, value.LocalTime.Hour);
        Assert.Equal(offsetHours, value.Offset?.TotalHours);
    }

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("2024:13:01 00:00:00")]
    [InlineData("2024:02:30 00:00:00")]
    [InlineData("garbage")]
    [InlineData("2024:05:01 14:03:03+8")]
    public void TryParse_RejectsInvalidValues(string text) => Assert.False(CaptureTime.TryParse(text, out _));

    [Fact]
    public void DistanceTo_BothWithOffsets_ComparesInstants()
    {
        CaptureTime.TryParse("2024:05:01 14:03:03+08:00", out var photo);
        CaptureTime.TryParse("2024:05:01 06:03:04+00:00", out var video);

        Assert.Equal(TimeSpan.FromSeconds(1), photo.DistanceTo(video));
    }

    [Fact]
    public void DistanceTo_OffsetMissingOnOneSide_ComparesLocalTimes()
    {
        CaptureTime.TryParse("2024:05:01 14:03:03", out var photo);
        CaptureTime.TryParse("2024:05:01 14:03:05+08:00", out var video);

        Assert.Equal(TimeSpan.FromSeconds(2), photo.DistanceTo(video));
    }

    [Fact]
    public void ToExifString_IncludesOffsetWhenKnown()
    {
        CaptureTime.TryParse("2024:05:01 14:03:03-03:30", out var value);

        Assert.Equal("2024:05:01 14:03:03-03:30", value.ToExifString());
        Assert.Equal("2024:05:01 14:03:03", (value with { Offset = null }).ToExifString());
    }
}
