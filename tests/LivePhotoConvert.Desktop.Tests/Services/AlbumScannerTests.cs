using System.Buffers.Binary;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Services;

public class AlbumScannerTests
{
    [Fact]
    public async Task ScanDirectoryAsync_SniffsExactDimensionsAndResolutions()
    {
        using var context = new TestSandbox();

        // 构造一个 1080x1920 (竖屏) 的真实 PNG 头部
        byte[] pngHeader = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(pngHeader, 0);
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(pngHeader.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(16, 4), 1080); // Width
        BinaryPrimitives.WriteUInt32BigEndian(pngHeader.AsSpan(20, 4), 1920); // Height

        context.CreateInputFile("IMG_9999.png", pngHeader);
        context.CreateInputFile("IMG_9999.mov", new byte[100]);

        var result = await AlbumScanner.ScanDirectoryAsync(new Localizer(), context.InputDirectory, 0, TestContext.Current.CancellationToken);

        Assert.Single(result.Groups);
        var card = Assert.Single(result.Groups[0].AllCards);

        // 验证已提前嗅探到真实分辨率与宽高比，而不是默认的 4:3
        Assert.Equal("1080×1920", card.ResolutionText);
        Assert.Equal(1080.0 / 1920.0, card.AspectRatio, precision: 4);
    }
}
