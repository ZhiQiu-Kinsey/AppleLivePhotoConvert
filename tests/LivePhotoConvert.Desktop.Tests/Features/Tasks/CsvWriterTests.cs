using System.Text;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

public class CsvWriterTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("IMG_0001.jpg", "IMG_0001.jpg")]
    [InlineData("a,b.jpg", "\"a,b.jpg\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\r\nline2", "\"line1\r\nline2\"")]
    [InlineData("中文 文件名.heic", "中文 文件名.heic")]
    public void EscapeField_FollowsRfc4180(string? value, string expected) =>
        Assert.Equal(expected, CsvWriter.EscapeField(value));

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-1.jpg", "'-1.jpg")]
    [InlineData("@HYPERLINK", "'@HYPERLINK")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("=a,\"b\"", "\"'=a,\"\"b\"\"\"")]
    [InlineData("a=b", "a=b")]
    public void EscapeField_PrefixesFormulaTriggers(string value, string expected) =>
        Assert.Equal(expected, CsvWriter.EscapeField(value));

    [Fact]
    public void Write_UsesCommaSeparatorAndCrLf()
    {
        using var writer = new StringWriter();

        CsvWriter.Write(writer, [["a", "b,c"], [null, "=1"]]);

        Assert.Equal("a,\"b,c\"\r\n,'=1\r\n", writer.ToString());
    }

    [Fact]
    public void WriteFile_WritesUtf8BomAndLeavesNoTemporaryFiles()
    {
        var dir = Directory.CreateTempSubdirectory("lpc-csv-").FullName;
        try
        {
            var path = Path.Combine(dir, "report.csv");
            File.WriteAllText(path, "old content that is longer than the new one");

            CsvWriter.WriteFile(path, [["状态", "文件名"], ["成功", "IMG,1.jpg"]]);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal("状态,文件名\r\n成功,\"IMG,1.jpg\"\r\n", Encoding.UTF8.GetString(bytes[3..]));
            Assert.Equal(["report.csv"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
