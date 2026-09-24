using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Metadata;

public class ExifToolJsonTests
{
    [Fact]
    public void ParseMetadata_Photo_CombinesDateTimeOriginalWithOffset()
    {
        const string json = """
                            [{"SourceFile":"a.jpg","ExifIFD:DateTimeOriginal":"2024:05:01 14:03:03","ExifIFD:OffsetTimeOriginal":"+08:00",
                              "Apple:ContentIdentifier":"UUID-1","Composite:GPSLatitude":31.2,"Composite:GPSLongitude":-121.5,"Composite:GPSAltitude":12,
                              "IFD0:Make":"Apple","IFD0:Model":"iPhone 15 Pro"}]
                            """;

        var metadata = Assert.Single(ExifToolJson.ParseMetadata(json));

        Assert.Equal("a.jpg", metadata.Path);
        Assert.Equal(TimeSpan.FromHours(8), metadata.CaptureTime?.Offset);
        Assert.Equal("UUID-1", metadata.ContentIdentifier);
        Assert.Equal(new GeoLocation(31.2, -121.5, 12), metadata.Location);
        Assert.Equal("iPhone 15 Pro", metadata.Model);
    }

    [Fact]
    public void ParseMetadata_Video_PrefersKeysCreationDateAndDetectsMirror()
    {
        const string json = """
                            [{"SourceFile":"a.mov","QuickTime:CreateDate":"2024:05:01 06:03:02+00:00","Keys:CreationDate":"2024:05:01 14:03:02+08:00",
                              "Keys:ContentIdentifier":"UUID-1","QuickTime:Duration":2.5,
                              "QuickTime:MatrixStructure":"1 0 0 0 1 0 0 0 1","Track1:MatrixStructure":"-1 0 0 0 1 0 0 0 1"}]
                            """;

        var metadata = Assert.Single(ExifToolJson.ParseMetadata(json));

        Assert.Equal(14, metadata.CaptureTime?.LocalTime.Hour);
        Assert.Equal(TimeSpan.FromSeconds(2.5), metadata.Duration);
        Assert.True(metadata.IsMirrored);
    }

    [Fact]
    public void ParseMetadata_StillImageTime_SubtractsSampleDuration()
    {
        const string json = """
                            [{"SourceFile":"a.mov","Track1:TrackDuration":2.9,"Track3:TrackDuration":1.5333,"Track3:MediaDuration":0.0333,"Track3:StillImageTime":-1}]
                            """;

        Assert.Equal(1_500_000, Assert.Single(ExifToolJson.ParseMetadata(json)).StillImageTimeUs);
    }

    [Theory]
    [InlineData("0 1 0 1 0 0 0 0 1", true)]
    [InlineData("0 1 0 -1 0 0 0 0 1", false)]
    [InlineData("1 0 0 0 1 0 0 0 1", false)]
    [InlineData("1 0", false)]
    public void IsMirrorMatrix_UsesDeterminantSign(string matrix, bool mirrored) => Assert.Equal(mirrored, ExifToolJson.IsMirrorMatrix(matrix));

    [Fact]
    public void ParseMetadata_EmptyOrInvalidInput_YieldsNothing()
    {
        Assert.Empty(ExifToolJson.ParseMetadata(""));
        Assert.Empty(ExifToolJson.ParseMetadata("{}"));
    }
}
