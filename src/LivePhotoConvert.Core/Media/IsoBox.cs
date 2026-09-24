using System.Buffers.Binary;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// ISOBMFF（MP4 / MOV / HEIF）的 box 头。
/// </summary>
/// <param name="Size">box 总长（含头）；按原样读取时 0 表示延续到外层容器末尾</param>
/// <param name="Type">四字符类型（大端）</param>
/// <param name="HeaderLength">头长度：8，或带 64 位长度时为 16</param>
internal readonly record struct IsoBoxHeader(long Size, uint Type, int HeaderLength);

/// <summary>
/// 从流中逐个读取 box 头，只做小块读取。
/// </summary>
internal static class IsoBox
{
    public const uint Ftyp = 0x66747970; // "ftyp"
    public const uint Meta = 0x6D657461; // "meta"
    public const uint Mpvd = 0x6D707664; // "mpvd"

    /// <summary>
    /// 按原样读取 <paramref name="offset"/> 处的 box 头：32 位长度 + 类型，长度为 1 时后跟 64 位长度。
    /// 不足一个头或 64 位长度超出 <see cref="long"/> 时返回 <c>null</c>；长度 0 不做解释。
    /// </summary>
    public static IsoBoxHeader? ReadHeader(Stream stream, long offset)
    {
        if (offset < 0 || offset > stream.Length - 8)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[16];
        stream.Position = offset;
        if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
        {
            return null;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (size != 1)
        {
            return new IsoBoxHeader(size, type, 8);
        }

        if (stream.ReadAtLeast(header[8..], 8, throwOnEndOfStream: false) < 8)
        {
            return null;
        }

        var largeSize = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        return largeSize > long.MaxValue ? null : new IsoBoxHeader((long)largeSize, type, 16);
    }

    /// <summary>
    /// 读取位于 [<paramref name="offset"/>, <paramref name="end"/>) 内的 box 头，并把长度 0 解析为延续到 <paramref name="end"/>。
    /// 长度小于头或越过 <paramref name="end"/> 视为结构损坏，返回 <c>null</c>。
    /// </summary>
    public static IsoBoxHeader? ReadBounded(Stream stream, long offset, long end)
    {
        if (ReadHeader(stream, offset) is not { } box)
        {
            return null;
        }

        var size = box.Size == 0 ? end - offset : box.Size;
        return size < box.HeaderLength || size > end - offset ? null : box with { Size = size };
    }
}

/// <summary>
/// 已读入内存的一个 box：类型与内容（不含头）。
/// </summary>
internal readonly ref struct IsoBoxSpan(uint type, ReadOnlySpan<byte> body)
{
    private readonly ReadOnlySpan<byte> _body = body;

    public uint Type => type;

    public ReadOnlySpan<byte> Body => _body;

    public void Deconstruct(out uint boxType, out ReadOnlySpan<byte> boxBody)
    {
        boxType = type;
        boxBody = _body;
    }
}

/// <summary>
/// 在内存中依次枚举相邻的 box（支持 64 位长度与延续到末尾的长度 0），遇到畸形 box 即停止。
/// </summary>
internal ref struct IsoBoxEnumerator(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _next;

    public IsoBoxSpan Current { get; private set; }

    public readonly IsoBoxEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        var remaining = _data[_next..];
        if (remaining.Length < 8)
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        var headerLength = 8;
        if (size == 1)
        {
            if (remaining.Length < 16)
            {
                return false;
            }

            var largeSize = BinaryPrimitives.ReadUInt64BigEndian(remaining[8..]);
            size = largeSize > (ulong)remaining.Length ? -1 : (long)largeSize;
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = remaining.Length;
        }

        if (size < headerLength || size > remaining.Length)
        {
            return false;
        }

        Current = new IsoBoxSpan(BinaryPrimitives.ReadUInt32BigEndian(remaining[4..]), remaining[headerLength..(int)size]);
        _next += (int)size;
        return true;
    }
}
