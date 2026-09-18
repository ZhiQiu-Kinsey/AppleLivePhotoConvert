using System.Buffers.Binary;
using LivePhotoConvert.Core.Matching;

namespace LivePhotoConvert.Core.Tests;

public class FastImageHeaderReaderTests
{
    [Fact]
    public void ReadPng_ValidHeader_ExtractsDimensionsAccurately()
    {
        // 8 bytes PNG signature + 4 bytes length (13) + 4 bytes "IHDR" + 4 bytes width + 4 bytes height
        byte[] png = new byte[32];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png, 0);

        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 1920);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 1080);

        using var ms = new MemoryStream(png);
        bool success = FastImageHeaderReader.TryReadDimensions(ms, ".png", out var dims);

        Assert.True(success);
        Assert.Equal(1920, dims.Width);
        Assert.Equal(1080, dims.Height);
        Assert.Equal(1920.0 / 1080.0, dims.AspectRatio, precision: 4);
    }

    [Fact]
    public void ReadJpeg_BaselineSof0_ExtractsDimensionsAccurately()
    {
        using var ms = new MemoryStream();
        // SOI: FF D8
        ms.Write([0xFF, 0xD8]);

        // APP0 segment: FF E0, length 16 (0x00, 0x10), 14 bytes payload
        ms.Write([0xFF, 0xE0, 0x00, 0x10]);
        ms.Write(new byte[14]);

        // SOF0 segment: FF C0, length 17 (0x00, 0x11), precision 8, height 3000, width 4000, 3 components
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        byte[] hw = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(hw.AsSpan(0, 2), 3000); // height
        BinaryPrimitives.WriteUInt16BigEndian(hw.AsSpan(2, 2), 4000); // width
        ms.Write(hw);
        ms.Write(new byte[10]);

        ms.Seek(0, SeekOrigin.Begin);
        bool success = FastImageHeaderReader.TryReadDimensions(ms, ".jpg", out var dims);

        Assert.True(success);
        Assert.Equal(4000, dims.Width);
        Assert.Equal(3000, dims.Height);
        Assert.Equal(4000.0 / 3000.0, dims.AspectRatio, precision: 4);
    }

    [Fact]
    public void ReadJpeg_WithExifOrientation6_SwapsWidthAndHeightToPortrait()
    {
        using var ms = new MemoryStream();
        // SOI
        ms.Write([0xFF, 0xD8]);

        // APP1 Exif segment with Orientation = 6 (Rotate 90 CW)
        using (var app1 = new MemoryStream())
        {
            app1.Write("Exif\0\0"u8);
            // TIFF header: "II" (little endian), 0x002A, offset 8 to IFD0
            app1.Write([0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
            // IFD0: 1 entry
            app1.Write([0x01, 0x00]);
            // Entry: Tag 0x0112 (Orientation), Type 3 (SHORT), Count 1, Value 6
            app1.Write([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00]);

            byte[] app1Bytes = app1.ToArray();
            ushort segLen = (ushort)(app1Bytes.Length + 2);
            byte[] segLenBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(segLenBytes, segLen);
            ms.Write([0xFF, 0xE1]);
            ms.Write(segLenBytes);
            ms.Write(app1Bytes);
        }

        // SOF0: sensor raw width 4032, height 3024
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        byte[] hw = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(hw.AsSpan(0, 2), 3024); // raw height
        BinaryPrimitives.WriteUInt16BigEndian(hw.AsSpan(2, 2), 4032); // raw width
        ms.Write(hw);
        ms.Write(new byte[10]);

        ms.Seek(0, SeekOrigin.Begin);
        bool success = FastImageHeaderReader.TryReadDimensions(ms, ".jpg", out var dims);

        Assert.True(success);
        // Orientation 6 causes 90 deg rotation, so display width is 3024 and height is 4032 (portrait)
        Assert.Equal(3024, dims.Width);
        Assert.Equal(4032, dims.Height);
        Assert.Equal(3024.0 / 4032.0, dims.AspectRatio, precision: 4);
    }

    [Fact]
    public void ReadHeic_WithIspeAndIrot_ExtractsAccurateDimensions()
    {
        using var ms = new MemoryStream();
        // 1. ftyp box
        byte[] ftyp = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(ftyp.AsSpan(0, 4), 24);
        "ftypheic"u8.CopyTo(ftyp.AsSpan(4, 8));
        ms.Write(ftyp);

        // 2. Build meta box content: iprp -> ipco -> ispe + irot
        using var metaContent = new MemoryStream();
        metaContent.Write([0x00, 0x00, 0x00, 0x00]); // FullBox version + flags

        using var ipcoContent = new MemoryStream();
        // ispe box: size 20, "ispe", version+flags (4), width 4000, height 3000
        byte[] ispeBox = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(ispeBox.AsSpan(0, 4), 20);
        "ispe"u8.CopyTo(ispeBox.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(ispeBox.AsSpan(12, 4), 4000); // width
        BinaryPrimitives.WriteUInt32BigEndian(ispeBox.AsSpan(16, 4), 3000); // height
        ipcoContent.Write(ispeBox);

        // irot box: size 9, "irot", 1 byte angle=1 (90 deg)
        byte[] irotBox = [0x00, 0x00, 0x00, 0x09, (byte)'i', (byte)'r', (byte)'o', (byte)'t', 0x01];
        ipcoContent.Write(irotBox);

        // ipco box wrap
        byte[] ipcoBytes = ipcoContent.ToArray();
        byte[] ipcoBoxHeader = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(ipcoBoxHeader.AsSpan(0, 4), (uint)(ipcoBytes.Length + 8));
        "ipco"u8.CopyTo(ipcoBoxHeader.AsSpan(4, 4));

        // iprp box wrap
        using var iprpContent = new MemoryStream();
        iprpContent.Write(ipcoBoxHeader);
        iprpContent.Write(ipcoBytes);
        byte[] iprpBytes = iprpContent.ToArray();
        byte[] iprpBoxHeader = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(iprpBoxHeader.AsSpan(0, 4), (uint)(iprpBytes.Length + 8));
        "iprp"u8.CopyTo(iprpBoxHeader.AsSpan(4, 4));

        metaContent.Write(iprpBoxHeader);
        metaContent.Write(iprpBytes);

        // meta box wrap
        byte[] metaBytes = metaContent.ToArray();
        byte[] metaBoxHeader = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(metaBoxHeader.AsSpan(0, 4), (uint)(metaBytes.Length + 8));
        "meta"u8.CopyTo(metaBoxHeader.AsSpan(4, 4));

        ms.Write(metaBoxHeader);
        ms.Write(metaBytes);

        ms.Seek(0, SeekOrigin.Begin);
        bool success = FastImageHeaderReader.TryReadDimensions(ms, ".heic", out var dims);

        Assert.True(success);
        // irot angle 1 rotates 90 deg -> width 3000, height 4000
        Assert.Equal(3000, dims.Width);
        Assert.Equal(4000, dims.Height);
    }

    [Fact]
    public void ReadCorruptedOrEmptyFile_ReturnsFalseGracefully()
    {
        using var ms = new MemoryStream([0x00, 0x01, 0x02]);
        bool success = FastImageHeaderReader.TryReadDimensions(ms, ".jpg", out var dims);
        Assert.False(success);
        Assert.Equal(0, dims.Width);
        Assert.Equal(0, dims.Height);

        bool successPng = FastImageHeaderReader.TryReadDimensions(ms, ".png", out _);
        Assert.False(successPng);

        bool successFile = FastImageHeaderReader.TryReadDimensions("non_existent_file.heic", out _);
        Assert.False(successFile);
    }
}
