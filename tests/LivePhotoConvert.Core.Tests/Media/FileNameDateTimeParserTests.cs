using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class FileNameDateTimeParserTests
{
    [Theory]
    [InlineData("IMG_20260905_124144.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("20260905_124144.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("IMG20260905124144.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("2026-09-05 12.41.44.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("2026-09-05-12-41-44.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("2026_09_05_12_41_44.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("2026.09.05_12.41.44.jpg", 2026, 9, 5, 12, 41, 44)]
    [InlineData("PXL_20260905_124144123.mp.jpg", 2026, 9, 5, 12, 41, 44)]
    public void Should_Parse_FullDateTime_Correctly(string fileName, int y, int m, int d, int h, int min, int s)
    {
        var success = FileNameDateTimeParser.TryParse(fileName, out var result);

        Assert.True(success);
        Assert.Equal(new DateTime(y, m, d, h, min, s, DateTimeKind.Local), result);
    }

    [Theory]
    [InlineData("2026_05_05_11_20_IMG_0277.HEIC", 2026, 5, 5, 11, 20, 0)]
    [InlineData("2026-05-05 11.20.jpg", 2026, 5, 5, 11, 20, 0)]
    [InlineData("2026-05-05_11-20.mov", 2026, 5, 5, 11, 20, 0)]
    public void Should_Parse_YearMonthDayHourMinute_Correctly(string fileName, int y, int m, int d, int h, int min, int s)
    {
        var success = FileNameDateTimeParser.TryParse(fileName, out var result);

        Assert.True(success);
        Assert.Equal(new DateTime(y, m, d, h, min, s, DateTimeKind.Local), result);
    }

    [Theory]
    [InlineData("IMG-20260905-WA0001.jpg", 2026, 9, 5)]
    [InlineData("VID-20260905-WA0012.mp4", 2026, 9, 5)]
    public void Should_Parse_WhatsApp_Date_Correctly(string fileName, int y, int m, int d)
    {
        var success = FileNameDateTimeParser.TryParse(fileName, out var result);

        Assert.True(success);
        Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Local), result);
    }

    [Fact]
    public void Should_Parse_UnixTimestampMilliseconds_Correctly()
    {
        // 1700000000000 对应 UTC 2023-11-14 22:13:20
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).LocalDateTime;

        var success = FileNameDateTimeParser.TryParse("mmexport1700000000000.jpg", out var result);
        Assert.True(success);
        Assert.Equal(expected, result);

        success = FileNameDateTimeParser.TryParse("wx_camera_1700000000000.mp4", out result);
        Assert.True(success);
        Assert.Equal(expected, result);

        success = FileNameDateTimeParser.TryParse("QQ_1700000000000.jpg", out result);
        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Should_Parse_UnixTimestampSeconds_Correctly()
    {
        // 1700000000 对应 UTC 2023-11-14 22:13:20
        var expected = DateTimeOffset.FromUnixTimeSeconds(1700000000L).LocalDateTime;

        var success = FileNameDateTimeParser.TryParse("IMG_1700000000.jpg", out var result);
        Assert.True(success);
        Assert.Equal(expected, result);

        success = FileNameDateTimeParser.TryParse("1700000000.mp4", out result);
        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2026-09-05_IMG_0001.jpg", 2026, 9, 5)]
    [InlineData("20260905_IMG_0001.jpg", 2026, 9, 5)]
    public void Should_Parse_DateOnly_Correctly(string fileName, int y, int m, int d)
    {
        var success = FileNameDateTimeParser.TryParse(fileName, out var result);

        Assert.True(success);
        Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Local), result);
    }

    [Theory]
    [InlineData("IMG_0001.jpg")]
    [InlineData("DSC_1234.mov")]
    [InlineData("random_text_without_date.jpg")]
    [InlineData("20260231_120000.jpg")] // 2月31日非法日期
    [InlineData("88888888_999999.jpg")] // 年份超出 2099
    public void Should_Return_False_For_Invalid_Or_No_Date(string fileName)
    {
        var success = FileNameDateTimeParser.TryParse(fileName, out _);

        Assert.False(success);
    }
}
