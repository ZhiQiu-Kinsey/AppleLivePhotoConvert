using System.Text;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Metadata;

public class ExifToolSessionTests
{
    [Theory]
    [InlineData("[{\"a\":1}]\n{ready3}\n", "[{\"a\":1}]\n")]
    [InlineData("<x:xmpmeta/>{ready3}\n", "<x:xmpmeta/>")]          // -b 输出没有尾随换行
    [InlineData("<x:xmpmeta/>{ready3}\r\n", "<x:xmpmeta/>")]        // Windows 换行
    [InlineData("{ready3}\n", "")]
    public void TryTakeResponse_MarkerAtEnd_ReturnsContentWithoutMarker(string output, string expected)
    {
        Assert.True(ExifToolSession.TryTakeResponse(new StringBuilder(output), "{ready3}", out var content));
        Assert.Equal(expected, content);
    }

    [Theory]
    [InlineData("<x:xmpmeta/>{ready3}")]      // 换行尚未到达
    [InlineData("<x:xmpmeta/>{ready")]
    [InlineData("partial output\n")]
    [InlineData("{ready31}\n")]               // 其它命令的标记
    [InlineData("")]
    public void TryTakeResponse_IncompleteOutput_KeepsWaiting(string output)
    {
        Assert.False(ExifToolSession.TryTakeResponse(new StringBuilder(output), "{ready3}", out _));
    }

    [Fact]
    public void TryTakeResponse_MarkerSplitAcrossChunks_CompletesOnLastChunk()
    {
        var buffer = new StringBuilder();
        string? content = null;
        foreach (var chunk in (string[])["<x:xmp", "meta/>{rea", "dy3}", "\n"])
        {
            buffer.Append(chunk);
            if (ExifToolSession.TryTakeResponse(buffer, "{ready3}", out var taken))
            {
                content = taken;
                Assert.Equal("\n", chunk);
            }
        }

        Assert.Equal("<x:xmpmeta/>", content);
    }
}
