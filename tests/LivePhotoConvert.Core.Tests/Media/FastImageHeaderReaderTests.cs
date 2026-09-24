using System.Buffers.Binary;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Media;

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
    public void ReadJpeg_MegabytesOfAppSegmentsBeforeSof_StillReadsDimensions()
    {
        // 人像模式照片的深度图放在扩展 XMP 中，SOF 之前有十几个 64KB 的 APP1 段
        var jpeg = SyntheticImages.Jpeg(64, 48);
        using var ms = new MemoryStream();
        ms.Write(jpeg.AsSpan(0, 2));
        var segment = new byte[4 + 65533];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), 65535);
        "http://ns.adobe.com/xmp/extension/\0"u8.CopyTo(segment.AsSpan(4));
        for (var i = 0; i < 16; i++)
        {
            ms.Write(segment);
        }

        ms.Write(jpeg.AsSpan(2));

        Assert.True(FastImageHeaderReader.TryReadDimensions(ms, ".jpg", out var dims));
        Assert.Equal((64, 48), (dims.Width, dims.Height));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadHeader_JpegExif_ParsesOrientationAndCaptureTime(bool bigEndian)
    {
        var jpeg = SyntheticImages.Jpeg(4032, 3024, orientation: 6, dateTimeOriginal: "2024:05:06 07:08:09", offsetTimeOriginal: "+08:00", bigEndian: bigEndian);

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(jpeg), ".jpg", out var header));

        Assert.Equal(new ImageHeader(3024, 4032, 6, new DateTime(2024, 5, 6, 7, 8, 9), TimeSpan.FromHours(8)), header);
        Assert.True(header.IsTransposed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void ReadHeader_TransposingOrientations_SwapDimensions(int orientation)
    {
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Jpeg(400, 300, orientation)), ".jpg", out var header));

        Assert.Equal((300, 400, orientation), (header.Width, header.Height, header.Orientation));
    }

    [Fact]
    public void ReadHeader_Orientation3_KeepsDimensions()
    {
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Jpeg(400, 300, 3)), ".jpg", out var header));

        Assert.Equal((400, 300, 3), (header.Width, header.Height, header.Orientation));
    }

    [Fact]
    public void ReadHeader_CaptureTimeBeyondInitialWindow_ReadsRestOfSegment()
    {
        // 字符串值被 MakerNote 之类的大块数据推到 16KB 窗口之外
        var jpeg = SyntheticImages.Jpeg(640, 480, dateTimeOriginal: "2023:01:02 03:04:05", valuePadding: 30_000);

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(jpeg), ".jpg", out var header));

        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), header.DateTimeOriginal);
        Assert.Null(header.OffsetTimeOriginal);
    }

    [Fact]
    public void ReadHeader_XmpSegmentBeforeExif_StillFindsExif()
    {
        var jpeg = SyntheticImages.Jpeg(640, 480, orientation: 8, dateTimeOriginal: "2023:01:02 03:04:05", xmpFirst: true);

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(jpeg), ".jpg", out var header));

        Assert.Equal((480, 640, 8), (header.Width, header.Height, header.Orientation));
        Assert.NotNull(header.DateTimeOriginal);
    }

    [Fact]
    public void ReadHeader_BlankOrMalformedCaptureTime_IsNull()
    {
        var blank = SyntheticImages.Jpeg(640, 480, dateTimeOriginal: "    :  :     :  :  ");
        var invalid = SyntheticImages.Jpeg(640, 480, dateTimeOriginal: "2023:13:40 25:00:00", offsetTimeOriginal: "garbage");

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(blank), ".jpg", out var blankHeader));
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(invalid), ".jpg", out var invalidHeader));

        Assert.Null(blankHeader.DateTimeOriginal);
        Assert.Null(invalidHeader.DateTimeOriginal);
        Assert.Null(invalidHeader.OffsetTimeOriginal);
    }

    [Fact]
    public void ReadHeader_TruncatedExif_DoesNotThrow()
    {
        var jpeg = SyntheticImages.Jpeg(640, 480, orientation: 6, dateTimeOriginal: "2023:01:02 03:04:05");
        for (var length = 0; length < jpeg.Length; length++)
        {
            FastImageHeaderReader.TryReadHeader(new MemoryStream(jpeg[..length]), ".jpg", out _);
        }

        // 把 IFD 偏移改到段外：只丢失 EXIF，尺寸仍然可读
        var corrupted = (byte[])jpeg.Clone();
        corrupted[4 + 2 + 6 + 4] = 0xF0;
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(corrupted), ".jpg", out var header));
        Assert.Equal((640, 480, 1), (header.Width, header.Height, header.Orientation));
    }

    [Fact]
    public void ReadHeader_HeifExifItem_ParsesCaptureTimeAndRotation()
    {
        // irot 3 = 逆时针 270°，等价于 EXIF 方向 6
        var heif = SyntheticImages.Heif(4032, 3024, rotation: 3, dateTimeOriginal: "2024:05:06 07:08:09", offsetTimeOriginal: "-05:00");

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(heif), ".heic", out var header));

        Assert.Equal(new ImageHeader(3024, 4032, 6, new DateTime(2024, 5, 6, 7, 8, 9), TimeSpan.FromHours(-5)), header);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 8)]
    [InlineData(2, 3)]
    public void ReadHeader_HeifRotation_MapsToExifOrientation(int? rotation, int expected)
    {
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Heif(400, 300, rotation)), ".heic", out var header));

        Assert.Equal(expected, header.Orientation);
        Assert.Equal(expected == 8 ? (300, 400) : (400, 300), (header.Width, header.Height));
        Assert.Null(header.DateTimeOriginal);
    }

    [Fact]
    public void ReadHeader_Heif_UsesPrimaryItemPropertiesRatherThanLargestExtent()
    {
        var heif = SyntheticImages.Heif(400, 300, thumbnailWidth: 5000, thumbnailHeight: 5000);

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(heif), ".heic", out var header));
        Assert.Equal((400, 300), (header.Width, header.Height));

        // 没有 ipma 时退化为面积最大的 ispe
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Heif(400, 300, thumbnailWidth: 5000, thumbnailHeight: 5000, withIpma: false)), ".heic", out var fallback));
        Assert.Equal((5000, 5000), (fallback.Width, fallback.Height));
    }

    [Fact]
    public void ReadHeader_UnknownExtension_SniffsMagic()
    {
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Heif(400, 300)), ".bin", out var heif));
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Jpeg(640, 480)), "", out var jpeg));

        Assert.Equal(400, heif.Width);
        Assert.Equal(640, jpeg.Width);
    }

    [Fact]
    public void ReadHeader_TruncatedHeif_DoesNotThrow()
    {
        var heif = SyntheticImages.Heif(4032, 3024, rotation: 1, dateTimeOriginal: "2024:05:06 07:08:09");
        for (var length = 0; length < heif.Length; length++)
        {
            FastImageHeaderReader.TryReadHeader(new MemoryStream(heif[..length]), ".heic", out _);
        }
    }

    [Fact]
    public void ReadDimensions_WrapsHeader()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("IMG_0001.JPG", SyntheticImages.Jpeg(4000, 3000, orientation: 6));

        Assert.True(FastImageHeaderReader.TryReadDimensions(path, out var dimensions));
        Assert.True(FastImageHeaderReader.TryReadHeader(path, out var header));

        Assert.Equal(new ImageDimensions(3000, 4000), dimensions);
        Assert.Equal(dimensions, header.Dimensions);
    }

    [Fact]
    public async Task ReadHeader_RealJpegAndHeic_MatchesExifTool()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var heifEnc = ExternalTools.RequireHeifEnc();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var jpeg = temp.Combine("sample.jpg");
        var heic = temp.Combine("sample.heic");

        var generated = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=640x480", "-frames:v", "1", jpeg], token);
        Assert.True(generated.Success, generated.StandardError);
        var tagged = await ProcessRunner.RunAsync(exiftool, ["-q", "-overwrite_original", "-DateTimeOriginal=2024:05:06 07:08:09", "-OffsetTimeOriginal=+08:00", "-Orientation#=6", jpeg], token);
        Assert.True(tagged.Success, tagged.StandardError);
        var encoded = await ProcessRunner.RunAsync(heifEnc, ["-q", "60", jpeg, "-o", heic], token);
        Assert.True(encoded.Success, encoded.StandardError);

        var expected = new ImageHeader(480, 640, 6, new DateTime(2024, 5, 6, 7, 8, 9), TimeSpan.FromHours(8));
        Assert.True(FastImageHeaderReader.TryReadHeader(jpeg, out var jpegHeader));
        Assert.Equal(expected, jpegHeader);
        Assert.True(FastImageHeaderReader.TryReadHeader(heic, out var heicHeader));
        Assert.Equal(expected, heicHeader);
    }

    [Fact]
    public void ReadHeader_AppleMakerNote_ReadsContentIdentifier()
    {
        var jpeg = SyntheticImages.Jpeg(64, 48, dateTimeOriginal: "2024:05:06 07:08:09", contentIdentifier: "5B1C0A9E-1111-2222-3333-444455556666");
        var heif = SyntheticImages.Heif(64, 48, contentIdentifier: "ID-HEIF");

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(jpeg), ".jpg", out var jpegHeader));
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(heif), ".heic", out var heifHeader));

        Assert.Equal("5B1C0A9E-1111-2222-3333-444455556666", jpegHeader.ContentIdentifier);
        Assert.Equal("ID-HEIF", heifHeader.ContentIdentifier);
    }

    [Fact]
    public void ReadHeader_ProductMakerNoteTemplate_RoundTrips()
    {
        // 合成流程写入配对标识用的模板：TIFF 位于 SOI + APP1 头 + "Exif\0\0" 之后
        var template = AppleMakerNote.BuildTemplateJpeg("TEMPLATE-ID");
        byte[] tiff = template[12..^2];
        var jpeg = SyntheticImages.Jpeg(64, 48);
        var app1Length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        byte[] patched = [0xFF, 0xD8, 0xFF, 0xE1, .. length, .. payload, .. jpeg.AsSpan(4 + app1Length)];

        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(patched), ".jpg", out var header));
        Assert.Equal("TEMPLATE-ID", header.ContentIdentifier);
    }

    [Fact]
    public void CaptureTime_FollowsExifToolPrecedence()
    {
        Assert.True(FastImageHeaderReader.TryReadHeader(new MemoryStream(SyntheticImages.Jpeg(64, 48, dateTimeDigitized: "2020:01:02 03:04:05")), ".jpg", out var digitizedOnly));
        Assert.True(FastImageHeaderReader.TryReadHeader(
            new MemoryStream(SyntheticImages.Jpeg(64, 48, dateTimeOriginal: "2021:01:02 03:04:05+09:00", offsetTimeOriginal: "+08:00", dateTimeDigitized: "2020:01:02 03:04:05")),
            ".jpg", out var both));

        Assert.Equal(new CaptureTime(new DateTime(2020, 1, 2, 3, 4, 5), null), digitizedOnly.CaptureTime);
        // 时间字符串自带偏移时优先于 OffsetTimeOriginal
        Assert.Equal(new CaptureTime(new DateTime(2021, 1, 2, 3, 4, 5), TimeSpan.FromHours(9)), both.CaptureTime);
    }

    [Fact]
    public async Task ReadHeader_ContentIdentifierWrittenByExifTool_IsRead()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var jpeg = temp.Combine("sample.jpg");
        var generated = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48", "-frames:v", "1", jpeg], token);
        Assert.True(generated.Success, generated.StandardError);

        await using var metadata = ExifToolMetadataService.Create(exiftool, maxSessions: 1);
        await metadata.WriteApplePhotoIdentifierAsync(jpeg, "0A1B2C3D-AAAA-BBBB-CCCC-DDDDEEEEFFFF", token);
        var expected = (await metadata.ReadAsync([jpeg], cancellationToken: token))[jpeg].ContentIdentifier;

        Assert.True(FastImageHeaderReader.TryReadHeader(jpeg, out var header));
        Assert.Equal("0A1B2C3D-AAAA-BBBB-CCCC-DDDDEEEEFFFF", expected);
        Assert.Equal(expected, header.ContentIdentifier);
    }
}
