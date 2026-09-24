using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 视频头部中配对校验需要的信息。
/// </summary>
/// <param name="CreationTimeUtc">mvhd 创建时间（QuickTime 规范为 UTC）</param>
/// <param name="Duration">mvhd 时长</param>
/// <param name="KeysCreationDate">QuickTime Keys 的 com.apple.quicktime.creationdate（带拍摄地偏移）</param>
/// <param name="ContentIdentifier">QuickTime Keys 的 com.apple.quicktime.content.identifier</param>
internal readonly record struct QuickTimeInfo(DateTime? CreationTimeUtc, TimeSpan? Duration, CaptureTime? KeysCreationDate, string? ContentIdentifier)
{
    /// <summary>
    /// 按 ExifToolJson 的取值优先级折算为元数据：拍摄时间先取 Keys:CreationDate，再取 mvhd；
    /// mvhd 按 ExifTool 在 QuickTimeUTC=1 下的做法换算为本机时区并带上偏移，保证 <see cref="PairValidator"/> 的比较结果一致。
    /// </summary>
    public MediaMetadata ToMetadata(string path) => new()
    {
        Path = path,
        CaptureTime = KeysCreationDate ?? (CreationTimeUtc is { } utc ? ToLocal(utc) : null),
        ContentIdentifier = ContentIdentifier,
        Duration = Duration
    };

    private static CaptureTime ToLocal(DateTime utc)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(utc);
        return new CaptureTime(DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified), offset);
    }
}

/// <summary>
/// 只读 box 头与 moov 中的 mvhd、meta（keys + ilst）读取 MOV/MP4 的配对信息，不依赖 ExifTool。
/// iPhone 的 moov 位于 mdat 之后，按 box 头逐级定位，只有几次小块读取。
/// </summary>
internal static class QuickTimeHeader
{
    private const uint Moov = 0x6D6F6F76; // "moov"
    private const uint Mvhd = 0x6D766864; // "mvhd"
    private const uint Udta = 0x75647461; // "udta"
    private const uint Hdlr = 0x68646C72; // "hdlr"
    private const uint Keys = 0x6B657973; // "keys"
    private const uint Ilst = 0x696C7374; // "ilst"
    private const uint Data = 0x64617461; // "data"

    /// <summary>顶层与 moov 内的 box 数量上限，防止畸形文件拖慢扫描。</summary>
    private const int MaxBoxes = 64;

    /// <summary>moov/meta 通常只有几百字节，超过上限视为异常不读。</summary>
    private const int MaxMetaBytes = 256 * 1024;

    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ReadOnlySpan<byte> CreationDateKey => "com.apple.quicktime.creationdate"u8;

    private static ReadOnlySpan<byte> ContentIdentifierKey => "com.apple.quicktime.content.identifier"u8;

    public static QuickTimeInfo Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static QuickTimeInfo Read(Stream stream)
    {
        if (FindChild(stream, 0, stream.Length, Moov) is not { } moov)
        {
            return default;
        }

        var (created, duration) = FindChild(stream, moov.Start, moov.End, Mvhd) is { } mvhd ? ReadMovieHeader(stream, mvhd.Start) : default;
        // Apple 写在 moov/meta，FFmpeg（use_metadata_tags）写在 moov/udta/meta，ExifTool 两处都读
        var meta = FindChild(stream, moov.Start, moov.End, IsoBox.Meta)
                   ?? (FindChild(stream, moov.Start, moov.End, Udta) is { } udta ? FindChild(stream, udta.Start, udta.End, IsoBox.Meta) : null);
        var (creationDate, identifier) = meta is { } found && found.End - found.Start <= MaxMetaBytes
            ? ReadKeys(stream, found.Start, (int)(found.End - found.Start))
            : default;
        return new QuickTimeInfo(created, duration, creationDate, identifier);
    }

    private static (DateTime? Created, TimeSpan? Duration) ReadMovieHeader(Stream stream, long start)
    {
        Span<byte> body = stackalloc byte[32];
        stream.Position = start;
        var read = stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false);
        if (read < 20)
        {
            return default;
        }

        // 版本 0 各字段 32 位；版本 1 的时间与时长为 64 位
        ulong seconds, timescale, units;
        if (body[0] == 1)
        {
            if (read < 32)
            {
                return default;
            }

            seconds = BinaryPrimitives.ReadUInt64BigEndian(body[4..]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
            units = BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
        }
        else
        {
            seconds = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
            units = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        }

        DateTime? created = seconds == 0 || seconds > (ulong)(DateTime.MaxValue - QuickTimeEpoch).TotalSeconds ? null : QuickTimeEpoch.AddSeconds(seconds);
        TimeSpan? duration = timescale == 0 || units == 0 || units is uint.MaxValue or ulong.MaxValue ? null : TimeSpan.FromSeconds((double)units / timescale);
        return (created, duration);
    }

    private static (CaptureTime? CreationDate, string? ContentIdentifier) ReadKeys(Stream stream, long start, int length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.Position = start;
            if (stream.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false) < length)
            {
                return default;
            }

            var meta = buffer.AsSpan(0, length);

            // QuickTime 的 meta 是普通 box，MP4（ISO）的是 FullBox，多 4 字节版本与标志
            if (meta.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(meta[4..]) is not (Hdlr or Keys or Ilst))
            {
                meta = meta[4..];
            }

            ReadOnlySpan<byte> keys = default, ilst = default;
            foreach (var (type, body) in Children(meta))
            {
                if (type == Keys)
                {
                    keys = body;
                }
                else if (type == Ilst)
                {
                    ilst = body;
                }
            }

            FindKeyIndices(keys, out var creationIndex, out var identifierIndex);
            if (creationIndex == 0 && identifierIndex == 0)
            {
                return default;
            }

            CaptureTime? creationDate = null;
            string? identifier = null;
            foreach (var (index, body) in Children(ilst))
            {
                if ((index == creationIndex || index == identifierIndex) && Utf8Value(body) is { } value)
                {
                    if (index == creationIndex)
                    {
                        creationDate = ParseCreationDate(value);
                    }
                    else
                    {
                        identifier = value.Trim() is { Length: > 0 } trimmed ? trimmed : null;
                    }
                }
            }

            return (creationDate, identifier);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>keys：版本与标志 | 条目数 | 每项（长度 | 命名空间 | 键名），ilst 以 1 起的序号引用。</summary>
    private static void FindKeyIndices(ReadOnlySpan<byte> keys, out uint creationIndex, out uint identifierIndex)
    {
        creationIndex = identifierIndex = 0;
        if (keys.Length < 8)
        {
            return;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(keys[4..]);
        var position = 8;
        for (uint index = 1; index <= count && position <= keys.Length - 8; index++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(keys[position..]);
            if (size < 8 || size > (uint)(keys.Length - position))
            {
                return;
            }

            var name = keys.Slice(position + 8, (int)size - 8);
            if (name.SequenceEqual(CreationDateKey))
            {
                creationIndex = index;
            }
            else if (name.SequenceEqual(ContentIdentifierKey))
            {
                identifierIndex = index;
            }

            position += (int)size;
        }
    }

    /// <summary>ilst 项内的 data box：类型（1 = UTF-8）| 区域 | 值。</summary>
    private static string? Utf8Value(ReadOnlySpan<byte> item)
    {
        foreach (var (type, body) in Children(item))
        {
            if (type == Data && body.Length >= 8 && (BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF) == 1)
            {
                return Encoding.UTF8.GetString(body[8..]);
            }
        }

        return null;
    }

    /// <summary>
    /// Apple 写入 <c>2024-05-06T07:08:09+0800</c>，偏移没有冒号；补上后交给 <see cref="CaptureTime.TryParse"/>。
    /// </summary>
    internal static CaptureTime? ParseCreationDate(string value)
    {
        var text = value.Trim();
        if (text.Length >= 5 && text[^5] is '+' or '-' && char.IsAsciiDigit(text[^4]) && char.IsAsciiDigit(text[^3]) && char.IsAsciiDigit(text[^2]) && char.IsAsciiDigit(text[^1]))
        {
            text = string.Concat(text.AsSpan(0, text.Length - 2), ":", text.AsSpan(text.Length - 2));
        }

        return CaptureTime.TryParse(text, out var time) ? time : null;
    }

    private static IsoBoxEnumerator Children(ReadOnlySpan<byte> data) => new(data);

    /// <returns>子 box 负载的 [Start, End)</returns>
    private static (long Start, long End)? FindChild(Stream stream, long start, long end, uint type)
    {
        var offset = start;
        for (var i = 0; i < MaxBoxes && offset + 8 <= end; i++)
        {
            if (IsoBox.ReadBounded(stream, offset, end) is not { } box)
            {
                return null;
            }

            if (box.Type == type)
            {
                return (offset + box.HeaderLength, offset + box.Size);
            }

            offset += box.Size;
        }

        return null;
    }
}
