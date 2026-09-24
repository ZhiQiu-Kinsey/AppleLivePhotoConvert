using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.UltraHdr;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Media.UltraHdr;

public class UltraHdrJpegWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// libultrahdr 2.0.2 为主图长度 407095、增益图长度 29461、相对偏移 402346 写出的 MPF 段内容（不含段头）。
    /// </summary>
    private const string LibUltraHdrMpfPayload =
        "4d504600" + "4d4d002a00000008" + "0003"
        + "b000000700000004" + "30313030"
        + "b001000400000001" + "00000002"
        + "b002000700000020" + "00000032"
        + "00000000"
        + "00030000" + "00063637" + "00000000" + "0000" + "0000"
        + "00000000" + "00007315" + "000623aa" + "0000" + "0000";

    [Fact]
    public void BuildMpfSegment_MatchesLibUltraHdrBytes()
    {
        var segment = UltraHdrJpegWriter.BuildMpfSegment(407095, 29461, 402346);

        Assert.Equal(UltraHdrJpegWriter.MpfSegmentLength, segment.Length);
        Assert.Equal("ffe20058", Convert.ToHexStringLower(segment.AsSpan(0, 4)));
        Assert.Equal(LibUltraHdrMpfPayload, Convert.ToHexStringLower(segment.AsSpan(4)));
    }

    [Fact]
    public async Task WriteAsync_OrdersSegmentsAndPointsMpfAtGainMap()
    {
        using var temp = new TempDirectory();
        var metadata = GainMapMetadata.FromAppleHeadroom(4);
        var primary = temp.Combine("primary.jpg");
        var gainMap = temp.Combine("gainmap.jpg");
        UltraHdrSamples.WritePrimary(primary);
        UltraHdrSamples.WriteGainMap(gainMap);
        var output = temp.Combine("uhdr.jpg");

        var layout = await UltraHdrJpegWriter.WriteAsync(primary, gainMap, metadata, output, Token);

        await using var stream = File.OpenRead(output);
        Assert.Equal(["APP0", "Exif", "XMP", "ICC", "ISO", "MPF"], Describe(stream, 0));
        Assert.Equal(["XMP", "ISO", "APP0"], Describe(stream, layout.GainMapOffset));

        Assert.Equal(stream.Length, layout.ImageEnd);
        Assert.Equal(layout.GainMapOffset, layout.MpfPrimaryLength);
        Assert.Equal(layout.GainMapLength, layout.DirectoryGainMapLength);
        Assert.True(layout is { PrimaryHasIsoVersion: true, GainMapHasXmp: true, GainMapHasIsoMetadata: true, MpfLittleEndian: false });

        // 主图原有的图像数据原样保留，增益图紧跟在主图 EOI 之后
        var bytes = await File.ReadAllBytesAsync(output, Token);
        var original = await File.ReadAllBytesAsync(primary, Token);
        Assert.True(bytes.AsSpan(0, (int)layout.GainMapOffset).EndsWith(original.AsSpan(BodyOffset(original))));
        Assert.Equal("ffd9ffd8", Convert.ToHexStringLower(bytes.AsSpan((int)layout.GainMapOffset - 2, 4)));
        Assert.Equal(Convert.ToHexStringLower(metadata.ToIsoPayload()), Convert.ToHexStringLower(IsoPayload(stream, layout.GainMapOffset)));
        Assert.Equal("00000000", Convert.ToHexStringLower(IsoPayload(stream, 0)));

        var imageLayout = MotionPhotoLayout.Inspect(output);
        Assert.True(imageLayout.HasGainMap);
        Assert.Null(imageLayout.Video);
        Assert.Contains("lpc:Marker=\"keep-me\"", MotionPhotoLayout.ReadJpegXmp(stream), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_PrimaryWithTrailingData_IsRejected()
    {
        using var temp = new TempDirectory();
        var primary = temp.Combine("primary.jpg");
        var gainMap = temp.Combine("gainmap.jpg");
        UltraHdrSamples.WritePrimary(primary);
        UltraHdrSamples.WriteGainMap(gainMap);
        await File.AppendAllTextAsync(primary, "trailer", Token);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UltraHdrJpegWriter.WriteAsync(primary, gainMap, GainMapMetadata.FromAppleHeadroom(4), temp.Combine("out.jpg"), Token));
    }

    [Fact]
    public async Task WriteAsync_ExistingMpfAndIsoInInputs_AreReplacedNotDuplicated()
    {
        using var temp = new TempDirectory();
        var first = await UltraHdrSamples.CreateUltraHdrAsync(temp, "first.jpg", cancellationToken: Token);
        var primaryOnly = temp.Combine("primary.jpg");
        var layout = UltraHdrJpegWriter.Inspect(first)!;
        await BinaryFileCopy(first, primaryOnly, layout.GainMapOffset);
        var gainMap = temp.Combine("gainmap.jpg");
        await BinaryFileCopy(first, gainMap, layout.GainMapLength, layout.GainMapOffset);

        var output = temp.Combine("second.jpg");
        var second = await UltraHdrJpegWriter.WriteAsync(primaryOnly, gainMap, GainMapMetadata.FromAppleHeadroom(8), output, Token);

        await using var stream = File.OpenRead(output);
        Assert.Equal(["APP0", "Exif", "XMP", "ICC", "ISO", "MPF"], Describe(stream, 0));
        Assert.Equal(["XMP", "ISO", "APP0"], Describe(stream, second.GainMapOffset));
        Assert.Equal(Convert.ToHexStringLower(GainMapMetadata.FromAppleHeadroom(8).ToIsoPayload()), Convert.ToHexStringLower(IsoPayload(stream, second.GainMapOffset)));
    }

    [Fact]
    public async Task RefreshPrimaryLength_AfterXmpRewrite_FixesOnlyThePrimaryLength()
    {
        using var temp = new TempDirectory();
        var path = await UltraHdrSamples.CreateUltraHdrAsync(temp, cancellationToken: Token);
        var before = UltraHdrJpegWriter.Inspect(path)!;
        Assert.False(UltraHdrJpegWriter.RefreshPrimaryLength(path));

        // 模拟 ExifTool 改写 XMP：主图变长，MPF 中的相对偏移仍然正确，但主图长度过时
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var xmp = MotionPhotoLayout.ReadJpegXmp(new MemoryStream(bytes))!;
        await File.WriteAllBytesAsync(path, SyntheticMedia.InsertXmp(bytes, xmp.Replace("keep-me", "keep-me-and-grow-longer", StringComparison.Ordinal)), Token);
        var stale = UltraHdrJpegWriter.Inspect(path)!;
        Assert.False(stale.IsPrimaryLengthConsistent);
        Assert.Equal(before.MpfPrimaryLength, stale.MpfPrimaryLength);
        Assert.Throws<InvalidDataException>(() => UltraHdrJpegWriter.Verify(path));

        Assert.True(UltraHdrJpegWriter.RefreshPrimaryLength(path));

        var fixedLayout = UltraHdrJpegWriter.Verify(path);
        Assert.Equal(stale.GainMapOffset, fixedLayout.MpfPrimaryLength);
        Assert.Equal(before.GainMapLength, fixedLayout.GainMapLength);
    }

    [Fact]
    public async Task Verify_DirectoryLengthMismatch_Fails()
    {
        using var temp = new TempDirectory();
        var path = await UltraHdrSamples.CreateUltraHdrAsync(temp, cancellationToken: Token);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var xmp = MotionPhotoLayout.ReadJpegXmp(new MemoryStream(bytes))!;
        var length = UltraHdrJpegWriter.Inspect(path)!.GainMapLength;
        await File.WriteAllBytesAsync(path, SyntheticMedia.InsertXmp(bytes, UltraHdrXmp.ApplyPrimary(xmp, length + 1)), Token);
        UltraHdrJpegWriter.RefreshPrimaryLength(path);

        var error = Assert.Throws<InvalidDataException>(() => UltraHdrJpegWriter.Verify(path));
        Assert.Contains("GainMap", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8 + 4, 0xFFFFFFFFu)]          // IFD 偏移
    [InlineData(8 + 34 + 8, 0xFFFFFFF0u)]     // MP Entry 表偏移
    public void Inspect_MpfOffsetNearUInt32Max_ReturnsNullWithoutThrowing(int position, uint value)
    {
        var mpf = UltraHdrJpegWriter.BuildMpfSegment(1000, 100, 900);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(position), value);
        byte[] jpeg = [0xFF, 0xD8, .. mpf, 0xFF, 0xDB, 0x00, 0x02, 0xFF, 0xD9];

        Assert.Null(UltraHdrJpegWriter.Inspect(new MemoryStream(jpeg)));
    }

    [Fact]
    public void Inspect_PlainJpeg_ReturnsNull()
    {
        Assert.Null(UltraHdrJpegWriter.Inspect(new MemoryStream(SyntheticMedia.Jpeg())));
        Assert.Null(UltraHdrJpegWriter.Inspect(new MemoryStream(SyntheticMedia.UltraHdrStill(SyntheticMedia.Jpeg(100)))));
    }

    [Fact]
    public async Task MotionPhotoFromUltraHdr_LocatesVideoAfterGainMap()
    {
        using var temp = new TempDirectory();
        var path = await UltraHdrSamples.CreateUltraHdrAsync(temp, cancellationToken: Token);
        var video = SyntheticMedia.Mp4(6000);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var xmp = MotionPhotoXmp.Apply(MotionPhotoLayout.ReadJpegXmp(new MemoryStream(bytes)), video.Length, 0);
        await File.WriteAllBytesAsync(path, SyntheticMedia.InsertXmp(bytes, xmp), Token);
        UltraHdrJpegWriter.RefreshPrimaryLength(path);
        var cover = UltraHdrJpegWriter.Verify(path);
        await File.WriteAllBytesAsync(path, [.. await File.ReadAllBytesAsync(path, Token), .. video], Token);

        var layout = MotionPhotoLayout.Inspect(path);

        Assert.True(layout.HasGainMap);
        Assert.Equal(new EmbeddedVideo(cover.ImageEnd, video.Length), layout.Video);
        Assert.Equal(cover.GainMapOffset, UltraHdrJpegWriter.Verify(path).MpfPrimaryLength);
    }

    [Fact]
    public async Task Strip_UltraHdrMotionPhoto_KeepsGainMapAndConsistentMpf()
    {
        using var temp = new TempDirectory();
        var cover = await UltraHdrSamples.CreateUltraHdrAsync(temp, "cover.jpg", cancellationToken: Token);
        var video = SyntheticMedia.Mp4(6000);
        var bytes = await File.ReadAllBytesAsync(cover, Token);
        var xmp = MotionPhotoXmp.Apply(MotionPhotoLayout.ReadJpegXmp(new MemoryStream(bytes)), video.Length, 0);
        var source = temp.CreateFile("MVIMG.jpg", [.. SyntheticMedia.InsertXmp(bytes, xmp), .. video]);
        UltraHdrJpegWriter.RefreshPrimaryLength(source);

        var report = await new MotionPhotoStripper(new FakeMetadataService(), new FakeImageConverter()).StripAsync(
            new StripRequest { Files = [source], ConvertToHeic = true }, cancellationToken: Token);

        Assert.Equal(OutcomeKind.Succeeded, Assert.Single(report.Items).Kind);
        Assert.Equal(".jpg", Path.GetExtension(Assert.Single(report.Items[0].Outputs)));
        var layout = MotionPhotoLayout.Inspect(source);
        Assert.Null(layout.Video);
        Assert.True(layout.HasGainMap);
        var stripped = UltraHdrJpegWriter.Verify(source);
        Assert.Equal(new FileInfo(source).Length, stripped.ImageEnd);
        Assert.Equal(["Primary", "GainMap"], MotionPhotoXmp.Parse(MotionPhotoLayout.ReadJpegXmp(File.OpenRead(source)))!.Items.Select(item => item.Semantic));
    }

    private static List<string> Describe(Stream stream, long start)
    {
        var segments = JpegSegments.ReadHeader(stream, start, out _)!;
        return
        [
            .. segments.Select(segment => segment.Marker switch
            {
                0xE0 => "APP0",
                0xE1 when JpegSegments.StartsWith(stream, segment, JpegSegments.ExifSignature) => "Exif",
                0xE1 when JpegSegments.StartsWith(stream, segment, JpegSegments.XmpSignature) => "XMP",
                0xE2 when JpegSegments.StartsWith(stream, segment, JpegSegments.IccSignature) => "ICC",
                0xE2 when JpegSegments.StartsWith(stream, segment, "urn:iso:std:iso:ts:21496:-1\0"u8) => "ISO",
                0xE2 when JpegSegments.StartsWith(stream, segment, JpegSegments.MpfSignature) => "MPF",
                var marker => $"0x{marker:X2}"
            })
        ];
    }

    private static byte[] IsoPayload(Stream stream, long start)
    {
        var namespaceLength = "urn:iso:std:iso:ts:21496:-1\0"u8.Length;
        var segment = JpegSegments.ReadHeader(stream, start, out _)!
                                  .First(segment => segment.Marker == 0xE2 && JpegSegments.StartsWith(stream, segment, "urn:iso:std:iso:ts:21496:-1\0"u8));
        return JpegSegments.ReadPayload(stream, segment)[namespaceLength..];
    }

    private static int BodyOffset(byte[] jpeg)
    {
        JpegSegments.ReadHeader(new MemoryStream(jpeg), 0, out var bodyOffset);
        return (int)bodyOffset;
    }

    private static async Task BinaryFileCopy(string source, string destination, long length, long offset = 0)
    {
        var bytes = await File.ReadAllBytesAsync(source, Token);
        await File.WriteAllBytesAsync(destination, bytes.AsSpan((int)offset, (int)length).ToArray(), Token);
    }
}
