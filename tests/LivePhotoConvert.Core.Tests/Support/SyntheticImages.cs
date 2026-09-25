using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 构造带 EXIF 的 JPEG、带 meta 结构的 HEIF 与带 mvhd 的 MOV，用于驱动头部解析与扫描逻辑。
/// </summary>
internal static class SyntheticImages
{
    /// <summary>
    /// SOI | APP1 Exif（IFD0：Orientation、ExifIFD 指针；ExifIFD：DateTimeOriginal、OffsetTimeOriginal）| SOF0 | EOI。
    /// </summary>
    /// <param name="valuePadding">IFD 与字符串值之间的填充字节，用于把值推到读取窗口之外</param>
    /// <param name="xmpFirst">在 Exif 段之前放一个 XMP APP1 段</param>
    public static byte[] Jpeg(int storedWidth, int storedHeight, int orientation = 1, string? dateTimeOriginal = null, string? offsetTimeOriginal = null,
                              bool bigEndian = false, int valuePadding = 0, bool xmpFirst = false, int trailingBytes = 0,
                              string? dateTimeDigitized = null, string? contentIdentifier = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        if (xmpFirst)
        {
            WriteSegment(stream, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>")]);
        }

        WriteSegment(stream, 0xE1, [.. "Exif\0\0"u8, .. Tiff(orientation, dateTimeOriginal, offsetTimeOriginal, bigEndian, valuePadding, dateTimeDigitized, contentIdentifier)]);
        var sof = new byte[15];
        sof[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)storedHeight);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)storedWidth);
        sof[5] = 3;
        WriteSegment(stream, 0xC0, sof);
        stream.Write(new byte[trailingBytes]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    /// <summary>
    /// TIFF 结构：IFD0 在偏移 8，ExifIFD 紧随其后，值放在填充之后；配对标识写成 Apple MakerNotes（与 ExifTool 读取的结构一致）。
    /// </summary>
    public static byte[] Tiff(int orientation, string? dateTimeOriginal, string? offsetTimeOriginal, bool bigEndian = false, int valuePadding = 0,
                              string? dateTimeDigitized = null, string? contentIdentifier = null)
    {
        // ExifIFD 条目按标签升序
        List<(int Tag, int Type, byte[] Value)> exif = [];
        if (dateTimeOriginal is not null)
        {
            exif.Add((0x9003, 2, Encoding.ASCII.GetBytes(dateTimeOriginal + "\0")));
        }

        if (dateTimeDigitized is not null)
        {
            exif.Add((0x9004, 2, Encoding.ASCII.GetBytes(dateTimeDigitized + "\0")));
        }

        if (offsetTimeOriginal is not null)
        {
            exif.Add((0x9011, 2, Encoding.ASCII.GetBytes(offsetTimeOriginal + "\0")));
        }

        if (contentIdentifier is not null)
        {
            exif.Add((0x927C, 7, AppleMakerNote(contentIdentifier)));
        }

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 2 * 12 + 4;
        const int exifIfdOffset = ifd0Offset + ifd0Size;
        var valuesOffset = exifIfdOffset + 2 + exif.Count * 12 + 4 + valuePadding;
        var tiff = new byte[valuesOffset + exif.Sum(e => e.Value.Length)];

        void U16(int at, int value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), (ushort)value);
            }
        }

        void U32(int at, int value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), (uint)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), (uint)value);
            }
        }

        void Entry(int at, int tag, int type, int count, int value)
        {
            U16(at, tag);
            U16(at + 2, type);
            U32(at + 4, count);
            if (type == 3)
            {
                U16(at + 8, value);
            }
            else
            {
                U32(at + 8, value);
            }
        }

        tiff[0] = tiff[1] = bigEndian ? (byte)'M' : (byte)'I';
        U16(2, 42);
        U32(4, ifd0Offset);
        U16(ifd0Offset, 2);
        Entry(ifd0Offset + 2, 0x0112, 3, 1, orientation);
        Entry(ifd0Offset + 14, 0x8769, 4, 1, exifIfdOffset);

        U16(exifIfdOffset, exif.Count);
        var entry = exifIfdOffset + 2;
        var value = valuesOffset;
        foreach (var (tag, type, bytes) in exif)
        {
            Entry(entry, tag, type, bytes.Length, value);
            bytes.CopyTo(tiff, value);
            entry += 12;
            value += bytes.Length;
        }

        return tiff;
    }

    /// <summary><c>"Apple iOS\0" | 版本 1 | "MM" | IFD（0x0011 ContentIdentifier）</c>，偏移相对 MakerNotes 起点。</summary>
    private static byte[] AppleMakerNote(string contentIdentifier)
    {
        var identifier = Encoding.ASCII.GetBytes(contentIdentifier + "\0");
        var note = new byte[14 + 2 + 12 + 4 + identifier.Length];
        "Apple iOS\0\0\u0001MM"u8.CopyTo(note);
        BinaryPrimitives.WriteUInt16BigEndian(note.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(note.AsSpan(16), 0x0011);
        BinaryPrimitives.WriteUInt16BigEndian(note.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt32BigEndian(note.AsSpan(20), (uint)identifier.Length);
        BinaryPrimitives.WriteUInt32BigEndian(note.AsSpan(24), 32);
        identifier.CopyTo(note, 32);
        return note;
    }

    /// <summary>
    /// 最小 HEIF：ftyp | meta（pitm、iinf、iloc v1、iprp/ipco/ipma）| mdat（Exif 项）。
    /// 项 1 为主图，项 2 为缩略图，项 3 为 Exif。
    /// </summary>
    /// <param name="rotation">irot 的角度（逆时针 90° 的倍数），为 <c>null</c> 时不写 irot</param>
    /// <param name="thumbnailWidth">缩略图 ispe 宽；大于主图时可验证按 ipma 取主图而非面积最大者</param>
    public static byte[] Heif(int width, int height, int? rotation = null, string? dateTimeOriginal = null, string? offsetTimeOriginal = null,
                              int thumbnailWidth = 320, int thumbnailHeight = 240, bool withIpma = true, string? contentIdentifier = null,
                              bool appleGainMap = false, bool toneMapItem = false)
    {
        byte[] exifItem = dateTimeOriginal is null && contentIdentifier is null
            ? []
            : [0, 0, 0, 6, .. "Exif\0\0"u8, .. Tiff(1, dateTimeOriginal, offsetTimeOriginal, bigEndian: true, contentIdentifier: contentIdentifier)];
        var ftyp = Box("ftyp", [.. "heic"u8, 0, 0, 0, 0, .. "mif1heic"u8]);

        // 先以占位偏移构造 meta 求出长度，再回填 Exif 在 mdat 中的绝对偏移
        var meta = Meta(0);
        var exifOffset = ftyp.Length + meta.Length + 8;
        meta = Meta(exifOffset);
        return [.. ftyp, .. meta, .. Box("mdat", exifItem)];

        byte[] Meta(int exifFileOffset)
        {
            var pitm = FullBox("pitm", 0, U16Bytes(1));
            List<byte> iinfBody = [.. U16Bytes(2 + (exifItem.Length > 0 ? 1 : 0) + (toneMapItem ? 1 : 0))];
            iinfBody.AddRange(Infe(1, "hvc1"));
            iinfBody.AddRange(Infe(2, "hvc1"));
            if (exifItem.Length > 0)
            {
                iinfBody.AddRange(Infe(3, "Exif"));
            }

            if (toneMapItem)
            {
                iinfBody.AddRange(Infe(4, "tmap"));
            }

            var iinf = FullBox("iinf", 0, [.. iinfBody]);

            // iloc v1：offset_size=4、length_size=4、base_offset_size=0、index_size=0
            List<byte> ilocBody = [0x44, 0x00, .. U16Bytes(1), .. U16Bytes(3), .. U16Bytes(0), .. U16Bytes(0), .. U16Bytes(1), .. U32Bytes(exifFileOffset), .. U32Bytes(exifItem.Length)];
            var iloc = FullBox("iloc", 1, [.. ilocBody]);

            List<byte> ipco = [.. FullBox("ispe", 0, [.. U32Bytes(thumbnailWidth), .. U32Bytes(thumbnailHeight)]), .. FullBox("ispe", 0, [.. U32Bytes(width), .. U32Bytes(height)])];
            if (rotation is { } angle)
            {
                ipco.AddRange(Box("irot", [(byte)angle]));
            }

            // 增益图属性排在最后，不改变前面属性的序号
            if (appleGainMap)
            {
                ipco.AddRange(FullBox("auxC", 0, [.. "urn:com:apple:photo:2020:aux:hdrgainmap"u8, 0]));
            }

            // 属性序号从 1 开始：1 = 缩略图 ispe，2 = 主图 ispe，3 = irot
            byte[] primaryAssociations = rotation is null ? [1, 0x82] : [2, 0x82, 0x83];
            var ipma = FullBox("ipma", 0, [.. U32Bytes(2), .. U16Bytes(1), .. primaryAssociations, .. U16Bytes(2), 1, 0x81]);
            var iprp = Box("iprp", withIpma ? [.. Box("ipco", [.. ipco]), .. ipma] : Box("ipco", [.. ipco]));
            return FullBox("meta", 0, [.. pitm, .. iinf, .. iloc, .. iprp]);
        }
    }

    /// <summary>
    /// ftyp | mdat | moov(mvhd [+ meta(hdlr, keys, ilst)])。mvhd 版本 0，创建时间为 1904 纪元秒数，时长以毫秒计。
    /// </summary>
    /// <param name="keysCreationDate">写入 com.apple.quicktime.creationdate，如 <c>2024-05-06T07:08:09+0800</c></param>
    /// <param name="isoMeta">meta 按 ISO 写成 FullBox（MP4 风格）</param>
    public static byte[] Mov(DateTime? creationTimeUtc, double durationSeconds = 0, string? keysCreationDate = null, string? contentIdentifier = null,
                             bool isoMeta = false, int mdatBytes = 2048)
    {
        var seconds = creationTimeUtc is { } utc ? (int)(uint)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0;
        var mvhd = FullBox("mvhd", 0, [.. U32Bytes(seconds), .. U32Bytes(seconds), .. U32Bytes(1000), .. U32Bytes((int)(durationSeconds * 1000)), .. new byte[80]]);
        List<(string Key, string Value)> entries = [];
        if (keysCreationDate is not null)
        {
            entries.Add(("com.apple.quicktime.creationdate", keysCreationDate));
        }

        if (contentIdentifier is not null)
        {
            entries.Add(("com.apple.quicktime.content.identifier", contentIdentifier));
        }

        byte[] moov = mvhd;
        if (entries.Count > 0)
        {
            var hdlr = FullBox("hdlr", 0, [0, 0, 0, 0, .. "mdta"u8, .. new byte[12], 0]);
            List<byte> keys = [.. U32Bytes(entries.Count)];
            List<byte> ilst = [];
            for (var i = 0; i < entries.Count; i++)
            {
                var name = Encoding.ASCII.GetBytes(entries[i].Key);
                keys.AddRange([.. U32Bytes(8 + name.Length), .. "mdta"u8, .. name]);
                var data = Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(entries[i].Value)]);
                ilst.AddRange([.. U32Bytes(8 + data.Length), .. U32Bytes(i + 1), .. data]);
            }

            byte[] children = [.. hdlr, .. FullBox("keys", 0, [.. keys]), .. Box("ilst", [.. ilst])];
            moov = [.. mvhd, .. (isoMeta ? FullBox("meta", 0, children) : Box("meta", children))];
        }

        return [.. Box("ftyp", [.. "qt  "u8, 0, 0, 0, 0, .. "qt  "u8]), .. Box("mdat", new byte[mdatBytes]), .. Box("moov", moov)];
    }

    private static byte[] Infe(int itemId, string type) =>
        FullBox("infe", 2, [.. U16Bytes(itemId), .. U16Bytes(0), .. Encoding.ASCII.GetBytes(type), 0]);

    public static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] FullBox(string type, byte version, byte[] body) => Box(type, [version, 0, 0, 0, .. body]);

    private static byte[] U16Bytes(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }

    private static byte[] U32Bytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        Span<byte> header = [0xFF, marker, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], checked((ushort)(payload.Length + 2)));
        stream.Write(header);
        stream.Write(payload);
    }
}
