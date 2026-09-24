using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Media.UltraHdr;

/// <summary>
/// 组装 Ultra HDR JPEG：主图 + 紧随其后的增益图，两套元数据（hdrgm XMP 与 ISO 21496-1）同时写入。
/// </summary>
/// <remarks>
/// 主图段顺序 APP0 → Exif → XMP(hdrgm + Container) → ICC → ISO(版本) → MPF，与 libultrahdr 输出一致；
/// 增益图段顺序 XMP(hdrgm 参数) → ISO(完整参数)。安卓 14 只认 XMP，iOS 18 / 安卓 15 起优先 ISO，缺一不可。
/// 主图熵编码数据流式复制，不整体读入内存。
/// </remarks>
public static class UltraHdrJpegWriter
{
    /// <summary>MPF 段总长：标记与长度 4 + "MPF\0" 4 + TIFF 头 8 + 3 项 IFD 42 + 两条 MP Entry 32。</summary>
    internal const int MpfSegmentLength = 90;

    /// <summary>增益图通常只有主图的 1/4 面积，超过此大小视为异常输入。</summary>
    private const long MaxGainMapBytes = 64L * 1024 * 1024;

    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// 把主图与增益图组装为 Ultra HDR JPEG 并回读校验。
    /// </summary>
    /// <param name="primaryJpegPath">主图（SDR 基础图），保留其 Exif、XMP 其它命名空间与 ICC</param>
    /// <param name="gainMapJpegPath">已按 ISO 语义编码的增益图 JPEG</param>
    /// <param name="metadata">增益图元数据</param>
    /// <param name="outputPath">输出文件，必须不存在</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="InvalidDataException">输入不是合法 JPEG，或写出结果校验失败</exception>
    public static async Task<UltraHdrLayout> WriteAsync(
        string primaryJpegPath,
        string gainMapJpegPath,
        GainMapMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var gainMap = BuildGainMap(await ReadGainMapAsync(gainMapJpegPath, cancellationToken), metadata);

        await using (var primary = new FileStream(primaryJpegPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var (prefix, suffix, bodyOffset) = BuildPrimaryHeader(primary, gainMap.Length);
            var bodyLength = primary.Length - bodyOffset;
            var primaryLength = prefix.Length + MpfSegmentLength + suffix.Length + bodyLength;
            // MPF 中的偏移相对 TIFF 头（段头 4 字节 + "MPF\0" 之后）
            var tiffStart = prefix.Length + 8L;
            var mpf = BuildMpfSegment(primaryLength, gainMap.Length, primaryLength - tiffStart);

            await using var output = new FileStream(outputPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = CopyBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                PreallocationSize = primaryLength + gainMap.Length
            });
            await output.WriteAsync(prefix, cancellationToken);
            await output.WriteAsync(mpf, cancellationToken);
            await output.WriteAsync(suffix, cancellationToken);
            primary.Position = bodyOffset;
            await primary.CopyToAsync(output, CopyBufferSize, cancellationToken);
            await output.WriteAsync(gainMap, cancellationToken);
        }

        return Verify(outputPath, expectedGainMapLength: gainMap.Length);
    }

    /// <summary>
    /// 读取文件的 Ultra HDR 结构；没有 MPF 或结构损坏时返回 <c>null</c>。
    /// </summary>
    public static UltraHdrLayout? Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return Inspect(stream);
    }

    public static UltraHdrLayout? Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var segments = JpegSegments.ReadHeader(stream, 0, out _);
        if (segments is null)
        {
            return null;
        }

        long? declaredGainMapLength = null;
        var hasIsoVersion = false;
        MpfEntries? mpf = null;
        foreach (var segment in segments)
        {
            if (segment.Marker == 0xE1 && declaredGainMapLength is null && JpegSegments.StartsWith(stream, segment, JpegSegments.XmpSignature))
            {
                var xmp = Encoding.UTF8.GetString(JpegSegments.ReadPayload(stream, segment).AsSpan(JpegSegments.XmpSignature.Length));
                declaredGainMapLength = MotionPhotoXmp.Parse(xmp)?.Items.FirstOrDefault(item => item.IsGainMap)?.Length;
            }
            else if (segment.Marker == 0xE2 && JpegSegments.StartsWith(stream, segment, GainMapMetadata.IsoNamespace))
            {
                hasIsoVersion = true;
            }
            else if (segment.Marker == 0xE2 && mpf is null && JpegSegments.StartsWith(stream, segment, JpegSegments.MpfSignature))
            {
                mpf = ReadMpf(stream, segment);
            }
        }

        if (mpf is not { } entries || entries.GainMapLength <= 0)
        {
            return null;
        }

        var gainMapOffset = entries.TiffStart + entries.GainMapRelativeOffset;
        if (gainMapOffset <= 0 || gainMapOffset + entries.GainMapLength > stream.Length)
        {
            return null;
        }

        var gainMapSegments = JpegSegments.ReadHeader(stream, gainMapOffset, out _);
        if (gainMapSegments is null)
        {
            return null;
        }

        var gainMapHasXmp = false;
        var gainMapHasIso = false;
        foreach (var segment in gainMapSegments)
        {
            if (segment.Marker == 0xE1 && JpegSegments.StartsWith(stream, segment, JpegSegments.XmpSignature))
            {
                gainMapHasXmp |= Encoding.UTF8.GetString(JpegSegments.ReadPayload(stream, segment)).Contains(GainMapMetadata.HdrgmNamespace, StringComparison.Ordinal);
            }
            else if (segment.Marker == 0xE2 && JpegSegments.StartsWith(stream, segment, GainMapMetadata.IsoNamespace))
            {
                gainMapHasIso |= segment.PayloadLength > GainMapMetadata.IsoNamespace.Length + GainMapMetadata.IsoVersionPayload.Length;
            }
        }

        return new UltraHdrLayout(
            gainMapOffset,
            entries.GainMapLength,
            entries.PrimaryLength,
            entries.PrimaryLengthPosition,
            entries.LittleEndian,
            declaredGainMapLength,
            hasIsoVersion,
            gainMapHasXmp,
            gainMapHasIso);
    }

    /// <summary>
    /// 校验 Ultra HDR 结构完整：MPF 主图长度与增益图位置、Container 目录的 GainMap 长度一致，两套元数据齐全，
    /// 且 <see cref="MotionPhotoLayout"/> 能识别出增益图。
    /// </summary>
    /// <param name="path">文件</param>
    /// <param name="expectedGainMapLength">期望的增益图长度；为 <c>null</c> 时不比较</param>
    /// <exception cref="InvalidDataException">结构不一致</exception>
    public static UltraHdrLayout Verify(string path, long? expectedGainMapLength = null)
    {
        var layout = Inspect(path) ?? throw new InvalidDataException("Ultra HDR 校验失败：找不到 MPF 或增益图。");
        string? problem = layout switch
        {
            { IsPrimaryLengthConsistent: false } => $"MPF 记录的主图长度 {layout.MpfPrimaryLength} 与增益图位置 {layout.GainMapOffset} 不一致",
            { DirectoryGainMapLength: null } => "XMP 的 Container 目录没有 GainMap 项",
            _ when layout.DirectoryGainMapLength != layout.GainMapLength => $"Container 目录的 GainMap 长度 {layout.DirectoryGainMapLength} 与 MPF 记录的 {layout.GainMapLength} 不一致",
            _ when expectedGainMapLength is { } expected && expected != layout.GainMapLength => $"增益图长度 {layout.GainMapLength} 与写入的 {expected} 不一致",
            { PrimaryHasIsoVersion: false } => "主图缺少 ISO 21496-1 版本段",
            { GainMapHasXmp: false } => "增益图缺少 hdrgm XMP",
            { GainMapHasIsoMetadata: false } => "增益图缺少 ISO 21496-1 元数据",
            _ => null
        };

        if (problem is null && !MotionPhotoLayout.Inspect(path).HasGainMap)
        {
            problem = "无法从 XMP 识别增益图";
        }

        return problem is null ? layout : throw new InvalidDataException($"Ultra HDR 校验失败：{problem}。");
    }

    /// <summary>
    /// ExifTool 改写主图元数据后，MPF 中的主图长度不会随之更新（增益图偏移是相对值，仍然正确）；
    /// 按增益图的实际位置修正该字段。结构不符合预期时不做任何修改。
    /// </summary>
    /// <returns>是否修改了文件</returns>
    public static bool RefreshPrimaryLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
        if (Inspect(stream) is not { IsPrimaryLengthConsistent: false } layout || layout.GainMapOffset > uint.MaxValue)
        {
            return false;
        }

        // 只在增益图紧跟主图 EOI 时修正；中间还有其它数据的文件结构未知，保持原样
        Span<byte> eoi = stackalloc byte[2];
        stream.Position = layout.GainMapOffset - 2;
        if (stream.ReadAtLeast(eoi, 2, throwOnEndOfStream: false) < 2 || eoi[0] != 0xFF || eoi[1] != 0xD9)
        {
            return false;
        }

        Span<byte> value = stackalloc byte[4];
        if (layout.MpfLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)layout.GainMapOffset);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(value, (uint)layout.GainMapOffset);
        }

        stream.Position = layout.MpfPrimaryLengthPosition;
        stream.Write(value);
        return true;
    }

    /// <summary>
    /// 重建主图头部：返回 MPF 之前与之后的段，以及图像数据的起点。
    /// </summary>
    private static (byte[] Prefix, byte[] Suffix, long BodyOffset) BuildPrimaryHeader(Stream primary, long gainMapLength)
    {
        var segments = JpegSegments.ReadHeader(primary, 0, out var bodyOffset)
                       ?? throw new InvalidDataException("主图不是有效的 JPEG。");
        EnsureEndsWithEoi(primary);

        var jfif = new List<byte[]>();
        var exif = new List<byte[]>();
        var xmpExtensions = new List<byte[]>();
        var icc = new List<byte[]>();
        var others = new List<byte[]>();
        string? existingXmp = null;
        foreach (var segment in segments)
        {
            if (segment.Marker == 0xE0)
            {
                jfif.Add(JpegSegments.ReadSegment(primary, segment));
            }
            else if (segment.Marker == 0xE1 && JpegSegments.StartsWith(primary, segment, JpegSegments.ExifSignature))
            {
                exif.Add(JpegSegments.ReadSegment(primary, segment));
            }
            else if (segment.Marker == 0xE1 && JpegSegments.StartsWith(primary, segment, JpegSegments.XmpSignature))
            {
                existingXmp ??= Encoding.UTF8.GetString(JpegSegments.ReadPayload(primary, segment).AsSpan(JpegSegments.XmpSignature.Length));
            }
            else if (segment.Marker == 0xE1 && JpegSegments.StartsWith(primary, segment, JpegSegments.XmpExtensionSignature))
            {
                xmpExtensions.Add(JpegSegments.ReadSegment(primary, segment));
            }
            else if (segment.Marker == 0xE2 && JpegSegments.StartsWith(primary, segment, JpegSegments.IccSignature))
            {
                icc.Add(JpegSegments.ReadSegment(primary, segment));
            }
            else if (segment.Marker == 0xE2 && (JpegSegments.StartsWith(primary, segment, JpegSegments.MpfSignature) || JpegSegments.StartsWith(primary, segment, GainMapMetadata.IsoNamespace)))
            {
                // 旧的 MPF / ISO 段由本次组装重新生成
            }
            else
            {
                others.Add(JpegSegments.ReadSegment(primary, segment));
            }
        }

        var xmp = JpegSegments.Build(0xE1, JpegSegments.XmpSignature, Encoding.UTF8.GetBytes(UltraHdrXmp.ApplyPrimary(existingXmp, gainMapLength)));
        var iso = JpegSegments.Build(0xE2, GainMapMetadata.IsoNamespace, GainMapMetadata.IsoVersionPayload);
        byte[] prefix = [0xFF, 0xD8, .. Concat(jfif), .. Concat(exif), .. xmp, .. Concat(xmpExtensions), .. Concat(icc), .. iso];
        return (prefix, Concat(others), bodyOffset);
    }

    /// <summary>
    /// 在增益图的 SOI 之后注入 hdrgm XMP 与 ISO 元数据，去掉它原有的同类段。
    /// </summary>
    private static byte[] BuildGainMap(byte[] jpeg, GainMapMetadata metadata)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        var segments = JpegSegments.ReadHeader(stream, 0, out var bodyOffset) ?? throw new InvalidDataException("增益图不是有效的 JPEG。");
        EnsureEndsWithEoi(stream);

        using var output = new MemoryStream(jpeg.Length + 1024);
        output.Write([0xFF, 0xD8]);
        output.Write(JpegSegments.Build(0xE1, JpegSegments.XmpSignature, Encoding.UTF8.GetBytes(metadata.ToGainMapXmp())));
        output.Write(JpegSegments.Build(0xE2, GainMapMetadata.IsoNamespace, metadata.ToIsoPayload()));
        foreach (var segment in segments)
        {
            var replaced = segment.Marker switch
            {
                0xE1 => JpegSegments.StartsWith(stream, segment, JpegSegments.XmpSignature) || JpegSegments.StartsWith(stream, segment, JpegSegments.ExifSignature),
                0xE2 => JpegSegments.StartsWith(stream, segment, GainMapMetadata.IsoNamespace) || JpegSegments.StartsWith(stream, segment, JpegSegments.MpfSignature),
                _ => false
            };
            if (!replaced)
            {
                output.Write(jpeg, (int)segment.Offset, segment.PayloadLength + 4);
            }
        }

        output.Write(jpeg, (int)bodyOffset, jpeg.Length - (int)bodyOffset);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadGainMapAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 4 or > MaxGainMapBytes)
        {
            throw new InvalidDataException($"增益图大小异常：{length} 字节。");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    /// <summary>
    /// 主图之后紧跟增益图，主图必须恰好以 EOI 结束，否则 MPF 偏移会指向错误位置。
    /// </summary>
    private static void EnsureEndsWithEoi(Stream stream)
    {
        Span<byte> tail = stackalloc byte[2];
        stream.Position = stream.Length - 2;
        if (stream.ReadAtLeast(tail, 2, throwOnEndOfStream: false) < 2 || tail[0] != 0xFF || tail[1] != 0xD9)
        {
            throw new InvalidDataException("JPEG 没有以 EOI 结尾（可能带有尾随数据），无法组装 Ultra HDR。");
        }
    }

    /// <summary>
    /// MPF（CIPA DC-007）：大端 TIFF 结构，一个 IFD 含版本、图像数与两条 MP Entry。
    /// </summary>
    internal static byte[] BuildMpfSegment(long primaryLength, long gainMapLength, long gainMapRelativeOffset)
    {
        if (primaryLength > uint.MaxValue || gainMapLength > uint.MaxValue || gainMapRelativeOffset is < 0 or > uint.MaxValue)
        {
            throw new InvalidDataException("图像超过 4 GB，MPF 无法表示。");
        }

        var segment = new byte[MpfSegmentLength];
        var span = segment.AsSpan();
        span[0] = 0xFF;
        span[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], MpfSegmentLength - 2);
        JpegSegments.MpfSignature.CopyTo(span[4..]);

        var tiff = span[8..];
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[8..], 3);
        WriteIfdEntry(tiff[10..], 0xB000, 7, 4, BinaryPrimitives.ReadUInt32BigEndian("0100"u8));
        WriteIfdEntry(tiff[22..], 0xB001, 4, 1, 2);
        const uint entriesOffset = 8 + 2 + 3 * 12 + 4;
        WriteIfdEntry(tiff[34..], 0xB002, 7, 32, entriesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[46..], 0);

        var entries = tiff[(int)entriesOffset..];
        // 主图：Baseline MP Primary Image（0x030000），偏移固定为 0
        BinaryPrimitives.WriteUInt32BigEndian(entries, 0x030000);
        BinaryPrimitives.WriteUInt32BigEndian(entries[4..], (uint)primaryLength);
        BinaryPrimitives.WriteUInt32BigEndian(entries[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[20..], (uint)gainMapLength);
        BinaryPrimitives.WriteUInt32BigEndian(entries[24..], (uint)gainMapRelativeOffset);
        return segment;
    }

    private static void WriteIfdEntry(Span<byte> entry, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(entry, tag);
        BinaryPrimitives.WriteUInt16BigEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], value);
    }

    private readonly record struct MpfEntries(long TiffStart, long PrimaryLength, long PrimaryLengthPosition, long GainMapLength, long GainMapRelativeOffset, bool LittleEndian);

    /// <summary>
    /// 解析 MPF 的 MP Entry 表；第一项为主图，第二项为增益图。字节序以 TIFF 头为准。
    /// </summary>
    private static MpfEntries? ReadMpf(Stream stream, JpegSegment segment)
    {
        var payload = JpegSegments.ReadPayload(stream, segment);
        var tiffStart = segment.PayloadOffset + JpegSegments.MpfSignature.Length;
        var tiff = payload.AsSpan(JpegSegments.MpfSignature.Length);
        if (tiff.Length < 8)
        {
            return null;
        }

        var littleEndian = tiff[..2].SequenceEqual("II"u8);
        if (!littleEndian && !tiff[..2].SequenceEqual("MM"u8))
        {
            return null;
        }

        uint U32(ReadOnlySpan<byte> s) => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        ushort U16(ReadOnlySpan<byte> s) => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

        var ifd = U32(tiff[4..]);
        if (ifd + 2 > tiff.Length)
        {
            return null;
        }

        var count = U16(tiff[(int)ifd..]);
        for (var i = 0; i < count; i++)
        {
            var entryStart = (int)ifd + 2 + i * 12;
            if (entryStart + 12 > tiff.Length)
            {
                return null;
            }

            var entry = tiff.Slice(entryStart, 12);
            if (U16(entry) != 0xB002)
            {
                continue;
            }

            var size = U32(entry[4..]);
            var offset = U32(entry[8..]);
            if (size < 32 || offset + size > tiff.Length)
            {
                return null;
            }

            var table = tiff.Slice((int)offset, 32);
            return new MpfEntries(
                tiffStart,
                U32(table[4..]),
                tiffStart + offset + 4,
                U32(table[20..]),
                U32(table[24..]),
                littleEndian);
        }

        return null;
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

/// <summary>
/// Ultra HDR JPEG 的结构。
/// </summary>
/// <param name="GainMapOffset">增益图 JPEG 在文件中的绝对位置，也是主图的实际长度</param>
/// <param name="GainMapLength">MPF 记录的增益图长度</param>
/// <param name="MpfPrimaryLength">MPF 记录的主图长度</param>
/// <param name="MpfPrimaryLengthPosition">MPF 主图长度字段的位置</param>
/// <param name="MpfLittleEndian">MPF 的字节序</param>
/// <param name="DirectoryGainMapLength">XMP Container 目录声明的 GainMap 长度</param>
/// <param name="PrimaryHasIsoVersion">主图带 ISO 21496-1 版本段</param>
/// <param name="GainMapHasXmp">增益图带 hdrgm XMP</param>
/// <param name="GainMapHasIsoMetadata">增益图带完整的 ISO 21496-1 元数据</param>
public sealed record UltraHdrLayout(
    long GainMapOffset,
    long GainMapLength,
    long MpfPrimaryLength,
    long MpfPrimaryLengthPosition,
    bool MpfLittleEndian,
    long? DirectoryGainMapLength,
    bool PrimaryHasIsoVersion,
    bool GainMapHasXmp,
    bool GainMapHasIsoMetadata)
{
    public bool IsPrimaryLengthConsistent => MpfPrimaryLength == GainMapOffset;

    /// <summary>主图 + 增益图的总长度，即动态照片中视频之前的部分。</summary>
    public long ImageEnd => GainMapOffset + GainMapLength;
}
