using System.Globalization;
using LivePhotoConvert.Desktop.Converters;

namespace LivePhotoConvert.Desktop.Tests.Converters;

public class MinWidthToBoolConverterTests
{
    [Theory]
    [InlineData(600.0, "500", true)]
    [InlineData(500.0, "500", true)]
    [InlineData(499.9, "500", false)]
    [InlineData(0.0, "500", false)]
    [InlineData(450.0, "<520", true)]
    [InlineData(519.9, "<520", true)]
    [InlineData(520.0, "<520", false)]
    [InlineData(600.0, "<520", false)]
    [InlineData(450.0, "!520", true)]
    [InlineData(520.0, "!520", false)]
    public void Convert_ComparesWidthWithThreshold(double width, string param, bool expected)
    {
        var result = MinWidthToBoolConverter.Instance.Convert(width, typeof(bool), param, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_InvalidInputs_ReturnsFalseAndConvertBackIsUnsupported()
    {
        var converter = MinWidthToBoolConverter.Instance;

        Assert.False((bool)converter.Convert("invalid", typeof(bool), "500", CultureInfo.InvariantCulture)!);
        Assert.False((bool)converter.Convert(null, typeof(bool), "500", CultureInfo.InvariantCulture)!);
        Assert.Throws<NotSupportedException>(() => converter.ConvertBack(true, typeof(double), null, CultureInfo.InvariantCulture));
    }
}
