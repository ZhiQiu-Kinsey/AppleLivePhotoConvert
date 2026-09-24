using System.Globalization;
using LivePhotoConvert.Desktop.Converters;

namespace LivePhotoConvert.Desktop.Tests.Converters;

public class ByteSizeConverterTests
{
    [Theory]
    [InlineData(-5L, "0 B")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.50 GB")]
    public void Format_UsesBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, ByteSizeConverter.Format(bytes));

    /// <summary>界面文案固定用点作小数点，不随线程区域变化。</summary>
    [Fact]
    public void Format_IgnoresThreadCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.5 KB", ByteSizeConverter.Format(1536));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Convert_AcceptsNumericBindingValues()
    {
        var converter = ByteSizeConverter.Instance;

        Assert.Equal("1.0 KB", converter.Convert(1024, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("1.0 KB", converter.Convert(1024.9, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 B", converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
