using System.Buffers;
using System.Buffers.Binary;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 图片显示宽高（已按方向转正）。
/// </summary>
public readonly record struct ImageDimensions(int Width, int Height)
{
    public double AspectRatio => Height > 0 ? (double)Width / Height : 4.0 / 3.0;
}

/// <summary>
/// 图片头部信息。
/// </summary>
/// <param name="Width">显示宽度（已按方向转正）</param>
/// <param name="Height">显示高度（已按方向转正）</param>
/// <param name="Orientation">显示前需施加的变换，取 EXIF Orientation 的 1～8；HEIC 由 irot 换算</param>
/// <param name="DateTimeOriginal">EXIF 拍摄时间（相机当地时间）</param>
/// <param name="OffsetTimeOriginal">EXIF 拍摄时间的 UTC 偏移</param>
/// <param name="DateTimeDigitized">EXIF 数字化时间（CreateDate），拍摄时间缺失时的后备</param>
/// <param name="ContentIdentifier">Apple MakerNotes 中的实况配对标识</param>
public readonly record struct ImageHeader(int Width, int Height, int Orientation = 1, DateTime? DateTimeOriginal = null, TimeSpan? OffsetTimeOriginal = null,
                                          DateTime? DateTimeDigitized = null, string? ContentIdentifier = null)
{
    /// <summary>
    /// 与 ExifTool 读取照片拍摄时间的优先级一致：DateTimeOriginal（配合 OffsetTimeOriginal），其次 ExifIFD 的 CreateDate。
    /// </summary>
    public CaptureTime? CaptureTime =>
        DateTimeOriginal is { } original ? new CaptureTime(original, OffsetTimeOriginal)
        : DateTimeDigitized is { } digitized ? new CaptureTime(digitized, null)
        : null;

    public double AspectRatio => Height > 0 ? (double)Width / Height : 4.0 / 3.0;

    public ImageDimensions Dimensions => new(Width, Height);

    /// <summary>方向 5～8 含 90° 旋转，存储宽高与显示宽高互换。</summary>
    public bool IsTransposed => Orientation is >= 5 and <= 8;
}

/// <summary>
/// 只读文件头部的图片信息嗅探：宽高、方向、EXIF 拍摄时间与 Apple 实况配对标识。
/// </summary>
/// <remarks>
/// 支持 JPEG（SOF + APP1 Exif 的 IFD0/ExifIFD）、PNG（IHDR）、HEIC/HEIF/AVIF（meta 中的 ispe/irot/ipma，
/// 以及 iinf/iloc 指向的 Exif 项）。不解码像素，单文件通常只需几 KB 读取。
/// </remarks>
public static class FastImageHeaderReader
{
    /// <summary>HEIC 顶层 box 的扫描上限，防止畸形文件拖慢扫描。</summary>
    private const int MaxScanBytes = 512 * 1024;

    /// <summary>
    /// JPEG 帧头（SOF）之前的扫描上限。人像模式等照片把深度图放在扩展 XMP 里，SOF 前可能有数 MB 的 APP 段；
    /// 各段按长度直接跳过，上限只防畸形文件逐字节找标记。
    /// </summary>
    private const int MaxJpegHeaderBytes = 16 * 1024 * 1024;

    /// <summary>JPEG APP1 首次读取量：IFD0 与 ExifIFD 通常位于段首几 KB，MakerNote 与内嵌缩略图在后部，不必读满 64KB。</summary>
    private const int InitialExifWindow = 16 * 1024;

    /// <summary>HEIC meta box 上限；手机 HEIC 的 meta 通常只有数 KB。</summary>
    private const int MaxHeifMetaBytes = 2 * 1024 * 1024;

    /// <summary>HEIC Exif 项读取上限：拍摄时间位于 IFD 前部，MakerNote 可以截断。</summary>
    private const int MaxHeifExifBytes = 64 * 1024;

    private const uint BoxPitm = 0x7069746D; // "pitm"
    private const uint BoxIinf = 0x69696E66; // "iinf"
    private const uint BoxIloc = 0x696C6F63; // "iloc"
    private const uint BoxIprp = 0x69707270; // "iprp"
    private const uint BoxIpco = 0x6970636F; // "ipco"
    private const uint BoxIpma = 0x69706D61; // "ipma"
    private const uint BoxIspe = 0x69737065; // "ispe"
    private const uint BoxIrot = 0x69726F74; // "irot"
    private const uint ItemExif = 0x45786966; // "Exif"

    private const ushort TypeAscii = 2;
    private const ushort TypeUndefined = 7;

    private static ReadOnlySpan<byte> ExifSignature => "Exif\0\0"u8;

    /// <summary>
    /// 尝试读取图片的显示宽高。
    /// </summary>
    public static bool TryReadDimensions(string filePath, out ImageDimensions dimensions)
    {
        var ok = TryReadHeader(filePath, out var header);
        dimensions = header.Dimensions;
        return ok;
    }

    /// <summary>
    /// 尝试从流中读取显示宽高。
    /// </summary>
    public static bool TryReadDimensions(Stream stream, string extension, out ImageDimensions dimensions)
    {
        var ok = TryReadHeader(stream, extension, out var header);
        dimensions = header.Dimensions;
        return ok;
    }

    /// <summary>
    /// 尝试读取图片头部信息；文件不存在、无权限或格式无法识别时返回 <c>false</c>。
    /// </summary>
    public static bool TryReadHeader(string filePath, out ImageHeader header)
    {
        header = default;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return TryReadHeader(stream, Path.GetExtension(filePath), out header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试从流中读取图片头部信息。流必须可定位，总是从起点读取；扩展名无法识别时按文件头魔数判断。
    /// </summary>
    public static bool TryReadHeader(Stream stream, string extension, out ImageHeader header)
    {
        ArgumentNullException.ThrowIfNull(stream);
        header = default;
        if (!stream.CanRead || !stream.CanSeek)
        {
            return false;
        }

        stream.Position = 0;
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadPng(stream, out header);
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadJpeg(stream, out header);
        }

        if (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadHeif(stream, out header);
        }

        Span<byte> magic = stackalloc byte[12];
        var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return magic[..read] switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => TryReadPng(stream, out header),
            [0xFF, 0xD8, ..] => TryReadJpeg(stream, out header),
            [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] => TryReadHeif(stream, out header),
            _ => false
        };
    }

    private static bool TryReadPng(Stream stream, out ImageHeader header)
    {
        header = default;
        Span<byte> buf = stackalloc byte[24];
        if (stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) < buf.Length
            || !buf[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            || !buf[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(buf[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(buf[20..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return false;
        }

        header = new ImageHeader((int)width, (int)height);
        return true;
    }

    private static bool TryReadJpeg(Stream stream, out ImageHeader header)
    {
        header = default;
        Span<byte> buf = stackalloc byte[5];
        if (stream.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2 || buf[0] != 0xFF || buf[1] != 0xD8)
        {
            return false;
        }

        var exif = ExifFields.Default;
        var exifParsed = false;
        int width = 0, height = 0;
        while (stream.Position < MaxJpegHeaderBytes)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                break;
            }

            if (b != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xFF);

            if (marker < 0 || marker is 0xDA or 0xD9)
            {
                break;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (stream.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2)
            {
                break;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(buf) - 2;
            if (payloadLength < 0)
            {
                break;
            }

            var segmentEnd = stream.Position + payloadLength;
            if (marker == 0xE1 && !exifParsed)
            {
                // 第一个 APP1 可能是 XMP 而非 Exif，只有签名命中才算读过
                exifParsed = TryReadJpegExif(stream, payloadLength, ref exif);
                stream.Position = segmentEnd;
                continue;
            }

            // SOF0～SOF15，排除 DHT(C4)/JPG(C8)/DAC(CC)
            if (marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF))
            {
                if (payloadLength >= 5 && stream.ReadAtLeast(buf, 5, throwOnEndOfStream: false) == 5)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(buf[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(buf[3..]);
                }

                break;
            }

            stream.Position = segmentEnd;
        }

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        header = Build(width, height, exif);
        return true;
    }

    private static bool TryReadJpegExif(Stream stream, int payloadLength, ref ExifFields fields)
    {
        // 先核对签名，XMP 等其它 APP1 段不必读入
        Span<byte> signature = stackalloc byte[6];
        if (payloadLength < ExifSignature.Length + 8
            || stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) < signature.Length
            || !signature.SequenceEqual(ExifSignature))
        {
            return false;
        }

        var tiffLength = payloadLength - ExifSignature.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(tiffLength);
        try
        {
            var window = Math.Min(tiffLength, InitialExifWindow);
            var read = stream.ReadAtLeast(buffer.AsSpan(0, window), window, throwOnEndOfStream: false);
            if (!ParseTiff(buffer.AsSpan(0, read), ref fields) && read == window && read < tiffLength)
            {
                read += stream.ReadAtLeast(buffer.AsSpan(read, tiffLength - read), tiffLength - read, throwOnEndOfStream: false);
                ParseTiff(buffer.AsSpan(0, read), ref fields);
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryReadHeif(Stream stream, out ImageHeader header)
    {
        header = default;
        var length = stream.Length;
        long offset = 0;
        while (offset + 8 <= length && offset < MaxScanBytes)
        {
            if (IsoBox.ReadBounded(stream, offset, length) is not { } box)
            {
                return false;
            }

            if (box.Type == IsoBox.Meta)
            {
                var payloadLength = box.Size - box.HeaderLength;
                stream.Position = offset + box.HeaderLength;
                return payloadLength is >= 4 and <= MaxHeifMetaBytes && TryReadHeifMeta(stream, (int)payloadLength, out header);
            }

            offset += box.Size;
        }

        return false;
    }

    private static bool TryReadHeifMeta(Stream stream, int payloadLength, out ImageHeader header)
    {
        header = default;
        var buffer = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            if (stream.ReadAtLeast(buffer.AsSpan(0, payloadLength), payloadLength, throwOnEndOfStream: false) < payloadLength)
            {
                return false;
            }

            // meta 是 FullBox，子 box 从 version/flags 之后开始
            var meta = new HeifMeta(buffer.AsSpan(4, payloadLength - 4));
            if (!meta.TryGetPrimaryGeometry(out var width, out var height, out var rotation))
            {
                return false;
            }

            var exif = ExifFields.Default;
            if (meta.TryGetExifExtent(out var exifOffset, out var exifLength))
            {
                ReadHeifExif(stream, exifOffset, exifLength, ref exif);
            }

            // HEIC 的方向由 irot 决定，解码器会自动应用；Exif 中的 Orientation 仅供参考，不采用
            var orientation = rotation switch { 1 => 8, 2 => 3, 3 => 6, _ => 1 };
            header = Build(width, height, exif with { Orientation = orientation });
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadHeifExif(Stream stream, long offset, long length, ref ExifFields fields)
    {
        if (offset <= 0 || length < 4 + 8 || offset >= stream.Length)
        {
            return;
        }

        var count = (int)Math.Min(Math.Min(length, MaxHeifExifBytes), stream.Length - offset);
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.Position = offset;
            var read = stream.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);
            if (read < 4)
            {
                return;
            }

            // Exif 项以 4 字节的 TIFF 头偏移开头，其后通常是 "Exif\0\0"
            var tiffStart = 4 + (long)BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (tiffStart < read)
            {
                ParseTiff(buffer.AsSpan((int)tiffStart, read - (int)tiffStart), ref fields);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ImageHeader Build(int width, int height, ExifFields exif)
    {
        var orientation = exif.Orientation is >= 1 and <= 8 ? exif.Orientation : 1;
        if (orientation is >= 5 and <= 8)
        {
            (width, height) = (height, width);
        }

        return new ImageHeader(width, height, orientation, exif.DateTimeOriginal, exif.OffsetTimeOriginal, exif.DateTimeDigitized, exif.ContentIdentifier);
    }

    /// <summary>
    /// 解析 TIFF 结构中 IFD0 的方向与 ExifIFD 的拍摄时间。
    /// </summary>
    /// <returns>引用的数据全部落在 <paramref name="tiff"/> 之内时为 <c>true</c>；否则需读入更多数据后重试</returns>
    private static bool ParseTiff(ReadOnlySpan<byte> tiff, ref ExifFields fields)
    {
        if (tiff.Length < 8)
        {
            return false;
        }

        bool littleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49)
        {
            littleEndian = true;
        }
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D)
        {
            littleEndian = false;
        }
        else
        {
            return true;
        }

        var reader = new TiffReader(tiff, littleEndian);
        if (reader.U16(2) != 0x002A)
        {
            return true;
        }

        var complete = true;
        uint exifIfd = 0;
        complete &= ReadIfd(reader, reader.U32(4), isExifIfd: false, ref fields, ref exifIfd);
        if (exifIfd != 0)
        {
            complete &= ReadIfd(reader, exifIfd, isExifIfd: true, ref fields, ref exifIfd);
        }

        return complete;
    }

    private static bool ReadIfd(TiffReader reader, uint ifdOffset, bool isExifIfd, ref ExifFields fields, ref uint exifIfd)
    {
        if (ifdOffset < 8 || ifdOffset > reader.Length - 2)
        {
            return ifdOffset < 8;
        }

        var count = reader.U16((int)ifdOffset);
        var complete = true;
        Span<char> chars = stackalloc char[32];
        var position = (int)ifdOffset + 2;
        for (var i = 0; i < count; i++, position += 12)
        {
            if (position > reader.Length - 12)
            {
                return false;
            }

            var tag = reader.U16(position);
            switch (tag)
            {
                case 0x0112 when !isExifIfd:
                    fields.Orientation = reader.U16(position + 8);
                    break;
                case 0x8769 when !isExifIfd:
                    exifIfd = reader.U32(position + 8);
                    break;
                case 0x9003 when isExifIfd:
                    if (reader.TryGetValue(position, TypeAscii, 64, out var original))
                    {
                        if (CaptureTime.TryParse(ToChars(original, chars), out var time))
                        {
                            // 与 ExifTool 读取一致：时间字符串自带的偏移优先于 OffsetTimeOriginal
                            fields.DateTimeOriginal = time.LocalTime;
                            fields.OffsetTimeOriginal = time.Offset ?? fields.OffsetTimeOriginal;
                        }
                    }
                    else
                    {
                        complete = false;
                    }

                    break;
                case 0x9004 when isExifIfd:
                    if (reader.TryGetValue(position, TypeAscii, 64, out var digitized))
                    {
                        if (CaptureTime.TryParse(ToChars(digitized, chars), out var time))
                        {
                            fields.DateTimeDigitized = time.LocalTime;
                        }
                    }
                    else
                    {
                        complete = false;
                    }

                    break;
                case 0x9011 when isExifIfd:
                    if (reader.TryGetValue(position, TypeAscii, 64, out var offset))
                    {
                        fields.OffsetTimeOriginal ??= CaptureTime.ParseOffset(ToChars(offset, chars));
                    }
                    else
                    {
                        complete = false;
                    }

                    break;
                case 0x927C when isExifIfd:
                    if (reader.TryGetValue(position, TypeUndefined, 1024 * 1024, out var makerNote))
                    {
                        fields.ContentIdentifier = ReadAppleContentIdentifier(makerNote) ?? fields.ContentIdentifier;
                    }
                    else
                    {
                        complete = false;
                    }

                    break;
            }
        }

        return complete;
    }

    /// <summary>
    /// Apple MakerNotes：<c>"Apple iOS\0" | 版本 | 字节序 | IFD</c>，IFD 中的偏移相对 MakerNotes 起点；配对标识为标签 0x0011。
    /// </summary>
    private static string? ReadAppleContentIdentifier(ReadOnlySpan<byte> note)
    {
        if (note.Length < 16 || !note.StartsWith("Apple iOS\0"u8))
        {
            return null;
        }

        var reader = new TiffReader(note, littleEndian: note[12] == (byte)'I' && note[13] == (byte)'I');
        var count = reader.U16(14);
        for (int i = 0, position = 16; i < count && position <= note.Length - 12; i++, position += 12)
        {
            if (reader.U16(position) == 0x0011 && reader.TryGetValue(position, TypeAscii, 256, out var value) && !value.IsEmpty)
            {
                var terminator = value.IndexOf((byte)0);
                var identifier = System.Text.Encoding.ASCII.GetString(terminator >= 0 ? value[..terminator] : value).Trim();
                return identifier.Length > 0 ? identifier : null;
            }
        }

        return null;
    }

    private static ReadOnlySpan<char> ToChars(ReadOnlySpan<byte> ascii, Span<char> destination)
    {
        var terminator = ascii.IndexOf((byte)0);
        if (terminator >= 0)
        {
            ascii = ascii[..terminator];
        }

        var length = Math.Min(ascii.Length, destination.Length);
        for (var i = 0; i < length; i++)
        {
            destination[i] = (char)ascii[i];
        }

        return destination[..length];
    }

    private record struct ExifFields(int Orientation, DateTime? DateTimeOriginal, TimeSpan? OffsetTimeOriginal, DateTime? DateTimeDigitized, string? ContentIdentifier)
    {
        public static ExifFields Default => new(1, null, null, null, null);
    }

    private readonly ref struct TiffReader(ReadOnlySpan<byte> data, bool littleEndian)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Length => _data.Length;

        public ushort U16(int offset) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(_data[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(_data[offset..]);

        public uint U32(int offset) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(_data[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(_data[offset..]);

        /// <summary>
        /// 读取单字节类型（ASCII/UNDEFINED）的值：不超过 4 字节时内联在条目里，否则条目存偏移。
        /// 类型不符或超长时视为缺失；值落在数据之外时返回 <c>false</c>，提示调用方读入更多数据。
        /// </summary>
        public bool TryGetValue(int entry, ushort type, uint maxCount, out ReadOnlySpan<byte> value)
        {
            value = default;
            var count = U32(entry + 4);
            if (U16(entry + 2) != type || count > maxCount)
            {
                return true;
            }

            if (count <= 4)
            {
                value = _data.Slice(entry + 8, (int)count);
                return true;
            }

            var offset = U32(entry + 8);
            if (offset > (uint)_data.Length || count > (uint)_data.Length - offset)
            {
                return false;
            }

            value = _data.Slice((int)offset, (int)count);
            return true;
        }
    }

    /// <summary>
    /// HEIF meta box 的子 box 视图：主图的 ispe/irot（经 ipma 关联）与 Exif 项在文件中的位置（iinf + iloc）。
    /// </summary>
    private readonly ref struct HeifMeta
    {
        private readonly ReadOnlySpan<byte> _pitm;
        private readonly ReadOnlySpan<byte> _iinf;
        private readonly ReadOnlySpan<byte> _iloc;
        private readonly ReadOnlySpan<byte> _ipco;
        private readonly ReadOnlySpan<byte> _ipma;

        public HeifMeta(ReadOnlySpan<byte> children)
        {
            foreach (var (type, body) in new IsoBoxEnumerator(children))
            {
                switch (type)
                {
                    case BoxPitm: _pitm = body; break;
                    case BoxIinf: _iinf = body; break;
                    case BoxIloc: _iloc = body; break;
                    case BoxIprp:
                        foreach (var (childType, childBody) in new IsoBoxEnumerator(body))
                        {
                            switch (childType)
                            {
                                case BoxIpco: _ipco = childBody; break;
                                case BoxIpma when _ipma.IsEmpty: _ipma = childBody; break;
                            }
                        }

                        break;
                }
            }
        }

        private uint? PrimaryItemId =>
            _pitm.Length >= 6 && _pitm[0] == 0 ? BinaryPrimitives.ReadUInt16BigEndian(_pitm[4..])
            : _pitm.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(_pitm[4..])
            : null;

        /// <summary>
        /// 取主图关联的 ispe 与 irot；缺少 pitm/ipma 时退化为面积最大的 ispe（排除缩略图与切片）。
        /// </summary>
        public bool TryGetPrimaryGeometry(out int width, out int height, out int rotation)
        {
            Span<ushort> associated = stackalloc ushort[64];
            var associatedCount = PrimaryItemId is { } primary ? FindAssociations(primary, associated) : -1;
            return (associatedCount >= 0 && TryGetGeometry(associated[..associatedCount], filter: true, out width, out height, out rotation))
                   || TryGetGeometry([], filter: false, out width, out height, out rotation);
        }

        private bool TryGetGeometry(ReadOnlySpan<ushort> associated, bool filter, out int width, out int height, out int rotation)
        {
            width = height = rotation = 0;
            var index = 0;
            long bestArea = 0;
            foreach (var (type, body) in new IsoBoxEnumerator(_ipco))
            {
                // ipma 中的属性序号从 1 开始
                index++;
                if (filter && !associated.Contains((ushort)index))
                {
                    continue;
                }

                if (type == BoxIspe && body.Length >= 12)
                {
                    var w = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    var h = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                    if (w is > 0 and <= int.MaxValue && h is > 0 and <= int.MaxValue && (long)w * h > bestArea)
                    {
                        bestArea = (long)w * h;
                        (width, height) = ((int)w, (int)h);
                    }
                }
                else if (type == BoxIrot && body.Length >= 1)
                {
                    rotation = body[0] & 3;
                }
            }

            return bestArea > 0;
        }

        /// <returns>主图关联的属性序号个数；ipma 缺失或没有该项时返回 -1</returns>
        private int FindAssociations(uint itemId, Span<ushort> destination)
        {
            var ipma = _ipma;
            if (ipma.Length < 8)
            {
                return -1;
            }

            var version = ipma[0];
            var wideIndex = (ipma[3] & 1) != 0;
            var entryCount = BinaryPrimitives.ReadUInt32BigEndian(ipma[4..]);
            var position = 8;
            for (uint i = 0; i < entryCount; i++)
            {
                var idLength = version < 1 ? 2 : 4;
                if (position + idLength + 1 > ipma.Length)
                {
                    return -1;
                }

                var id = version < 1 ? BinaryPrimitives.ReadUInt16BigEndian(ipma[position..]) : BinaryPrimitives.ReadUInt32BigEndian(ipma[position..]);
                position += idLength;
                int associations = ipma[position++];
                var entryLength = associations * (wideIndex ? 2 : 1);
                if (position + entryLength > ipma.Length)
                {
                    return -1;
                }

                if (id == itemId)
                {
                    var count = Math.Min(associations, destination.Length);
                    for (var j = 0; j < count; j++)
                    {
                        destination[j] = wideIndex
                            ? (ushort)(BinaryPrimitives.ReadUInt16BigEndian(ipma[(position + j * 2)..]) & 0x7FFF)
                            : (ushort)(ipma[position + j] & 0x7F);
                    }

                    return count;
                }

                position += entryLength;
            }

            return -1;
        }

        /// <summary>
        /// 定位第一个 Exif 项；只支持数据直接存放在文件中（construction_method 0）的情形。
        /// </summary>
        public bool TryGetExifExtent(out long offset, out long length)
        {
            offset = length = 0;
            return HeifItems.FindItemId(_iinf, ItemExif) is { } itemId && TryGetItemExtent(itemId, out offset, out length);
        }

        private bool TryGetItemExtent(uint itemId, out long offset, out long length)
        {
            offset = length = 0;
            var iloc = _iloc;
            if (iloc.Length < 8)
            {
                return false;
            }

            var version = iloc[0];
            var offsetSize = iloc[4] >> 4;
            var lengthSize = iloc[4] & 0xF;
            var baseOffsetSize = iloc[5] >> 4;
            var indexSize = version is 1 or 2 ? iloc[5] & 0xF : 0;
            var position = 6;
            if (!TryReadUInt(iloc, ref position, version < 2 ? 2 : 4, out var itemCount))
            {
                return false;
            }

            for (ulong i = 0; i < itemCount; i++)
            {
                if (!TryReadUInt(iloc, ref position, version < 2 ? 2 : 4, out var id))
                {
                    return false;
                }

                ulong constructionMethod = 0;
                if (version is 1 or 2 && !TryReadUInt(iloc, ref position, 2, out constructionMethod))
                {
                    return false;
                }

                if (!TryReadUInt(iloc, ref position, 2, out _)
                    || !TryReadUInt(iloc, ref position, baseOffsetSize, out var baseOffset)
                    || !TryReadUInt(iloc, ref position, 2, out var extentCount))
                {
                    return false;
                }

                for (ulong e = 0; e < extentCount; e++)
                {
                    if (!TryReadUInt(iloc, ref position, indexSize, out _)
                        || !TryReadUInt(iloc, ref position, offsetSize, out var extentOffset)
                        || !TryReadUInt(iloc, ref position, lengthSize, out var extentLength))
                    {
                        return false;
                    }

                    if (id == itemId && e == 0)
                    {
                        if ((constructionMethod & 0xF) != 0 || baseOffset + extentOffset > long.MaxValue || extentLength > long.MaxValue)
                        {
                            return false;
                        }

                        offset = (long)(baseOffset + extentOffset);
                        length = extentLength == 0 ? long.MaxValue : (long)extentLength;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadUInt(ReadOnlySpan<byte> data, ref int position, int size, out ulong value)
        {
            value = 0;
            if (size is not (0 or 2 or 4 or 8) || position + size > data.Length)
            {
                return false;
            }

            for (var i = 0; i < size; i++)
            {
                value = (value << 8) | data[position + i];
            }

            position += size;
            return true;
        }
    }
}
