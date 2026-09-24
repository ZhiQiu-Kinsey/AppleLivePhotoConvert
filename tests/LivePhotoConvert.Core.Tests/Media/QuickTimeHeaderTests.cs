using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Media;

public class QuickTimeHeaderTests
{
    private static readonly DateTime Created = new(2024, 5, 5, 23, 8, 9, DateTimeKind.Utc);

    [Fact]
    public void ReadsMvhdCreationTimeAndDurationAfterMdat()
    {
        var info = QuickTimeHeader.Read(new MemoryStream(SyntheticImages.Mov(Created, durationSeconds: 2.5)));

        Assert.Equal(new QuickTimeInfo(Created, TimeSpan.FromSeconds(2.5), null, null), info);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsKeysCreationDateAndContentIdentifier(bool isoMeta)
    {
        var mov = SyntheticImages.Mov(Created, 2, keysCreationDate: "2024-05-06T07:08:10+0800", contentIdentifier: "ABC-123", isoMeta: isoMeta);

        var info = QuickTimeHeader.Read(new MemoryStream(mov));

        Assert.Equal(new CaptureTime(new DateTime(2024, 5, 6, 7, 8, 10), TimeSpan.FromHours(8)), info.KeysCreationDate);
        Assert.Equal("ABC-123", info.ContentIdentifier);
    }

    [Fact]
    public void ToMetadata_PrefersKeysCreationDate_ElseMvhdAsLocalTimeWithOffset()
    {
        var keys = QuickTimeHeader.Read(new MemoryStream(SyntheticImages.Mov(Created, 2, keysCreationDate: "2024-05-06T07:08:10+0800"))).ToMetadata("a.mov");
        var mvhd = QuickTimeHeader.Read(new MemoryStream(SyntheticImages.Mov(Created, 2))).ToMetadata("b.mov");

        Assert.Equal(TimeSpan.FromHours(8), keys.CaptureTime?.Offset);
        Assert.Equal(TimeSpan.Zero, mvhd.CaptureTime?.DistanceTo(new CaptureTime(Created, TimeSpan.Zero)));
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(Created), mvhd.CaptureTime?.Offset);
        Assert.Equal(TimeSpan.FromSeconds(2), mvhd.Duration);
    }

    [Theory]
    [InlineData("2024-05-06T07:08:09+0800", 8.0)]
    [InlineData("2024-05-06T07:08:09-05:30", -5.5)]
    [InlineData("2024-05-06T07:08:09Z", 0.0)]
    public void ParseCreationDate_AcceptsAppleOffsetFormats(string text, double offsetHours)
    {
        Assert.Equal(new CaptureTime(new DateTime(2024, 5, 6, 7, 8, 9), TimeSpan.FromHours(offsetHours)), QuickTimeHeader.ParseCreationDate(text));
    }

    [Fact]
    public void ParseCreationDate_Garbage_ReturnsNull()
    {
        Assert.Null(QuickTimeHeader.ParseCreationDate("not a date"));
    }

    [Fact]
    public void MissingMoovOrZeroTime_ReturnsEmpty()
    {
        Assert.Equal(default, QuickTimeHeader.Read(new MemoryStream(SyntheticMedia.Mov())));
        Assert.Null(QuickTimeHeader.Read(new MemoryStream(SyntheticImages.Mov(null))).CreationTimeUtc);
    }

    [Fact]
    public void TruncatedFile_DoesNotThrow()
    {
        var mov = SyntheticImages.Mov(Created, 2, keysCreationDate: "2024-05-06T07:08:10+0800", contentIdentifier: "ABC-123");
        for (var length = 0; length < mov.Length; length += 3)
        {
            QuickTimeHeader.Read(new MemoryStream(mov[..length]));
        }
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        Assert.Equal(default, QuickTimeHeader.Read(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mov")));
    }

    [Fact]
    public async Task RealMovie_MatchesExifToolMetadata()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var withKeys = temp.Combine("keys.mov");
        var plain = temp.Combine("plain.mov");
        foreach (var (path, keys) in (List<(string, bool)>)[(withKeys, true), (plain, false)])
        {
            List<string> arguments = ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=30", "-t", "1.5", "-pix_fmt", "yuv420p",
                                      "-metadata", "creation_time=2024-05-05T23:08:09Z"];
            if (keys)
            {
                arguments.AddRange(["-movflags", "use_metadata_tags", "-metadata", "com.apple.quicktime.content.identifier=ABC-123",
                                    "-metadata", "com.apple.quicktime.creationdate=2024-05-06T07:08:10+0800"]);
            }

            var generated = await ProcessRunner.RunAsync(ffmpeg, [.. arguments, path], token);
            Assert.True(generated.Success, generated.StandardError);
        }

        await using var metadata = ExifToolMetadataService.Create(exiftool, maxSessions: 1);
        var expected = await metadata.ReadAsync([withKeys, plain], cancellationToken: token);
        foreach (var path in (string[])[withKeys, plain])
        {
            var actual = QuickTimeHeader.Read(path).ToMetadata(path);
            Assert.Equal(expected[path].ContentIdentifier, actual.ContentIdentifier);
            Assert.NotNull(actual.CaptureTime);
            Assert.Equal(TimeSpan.Zero, expected[path].CaptureTime?.DistanceTo(actual.CaptureTime.Value));
            Assert.Equal(expected[path].CaptureTime?.Offset, actual.CaptureTime.Value.Offset);
            Assert.Equal(expected[path].Duration!.Value.TotalSeconds, actual.Duration!.Value.TotalSeconds, 2);
        }
    }
}
