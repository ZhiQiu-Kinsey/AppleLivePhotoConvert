using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 读取 HEIF 的 item 信息（meta / iinf / infe），不解码图像。
/// </summary>
public static class HeifItems
{
    /// <summary>meta box 通常只有几十 KB，超过上限视为畸形文件。</summary>
    private const int MaxMetaBytes = 16 * 1024 * 1024;

    private const int MaxTopLevelBoxes = 64;

    /// <summary>
    /// 文件是否含 ISO 21496-1 增益图的 tmap 派生图像（iOS 18 起可能只写这种增益图）。
    /// </summary>
    public static bool HasToneMapItem(string path) => ContainsItemType(path, "tmap");

    /// <summary>
    /// 判断 meta 中是否有指定类型的 item；不是 HEIF 或读取失败时返回 <c>false</c>。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="itemType">四字符类型，如 tmap、grid、hvc1</param>
    public static bool ContainsItemType(string path, string itemType)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemType);
        if (itemType.Length != 4)
        {
            throw new ArgumentException("item 类型必须是 4 个字符。", nameof(itemType));
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return ReadMeta(stream) is { } meta && ContainsItemType(meta, Encoding.ASCII.GetBytes(itemType));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 在顶层 box 中找到 meta 并读出其内容（不含 box 头）。
    /// </summary>
    private static byte[]? ReadMeta(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        long offset = 0;
        for (var count = 0; count < MaxTopLevelBoxes && offset + 8 <= stream.Length; count++)
        {
            stream.Position = offset;
            if (stream.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (size == 1)
            {
                if (stream.ReadAtLeast(header[8..16], 8, throwOnEndOfStream: false) < 8)
                {
                    return null;
                }

                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header[8..]), long.MaxValue);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - offset;
            }

            if (size < headerLength || size > stream.Length - offset)
            {
                return null;
            }

            if (count == 0 && !header[4..8].SequenceEqual("ftyp"u8))
            {
                return null;
            }

            if (header[4..8].SequenceEqual("meta"u8))
            {
                if (size - headerLength > MaxMetaBytes)
                {
                    return null;
                }

                var meta = new byte[size - headerLength];
                stream.Position = offset + headerLength;
                stream.ReadExactly(meta);
                return meta;
            }

            offset += size;
        }

        return null;
    }

    internal static bool ContainsItemType(ReadOnlySpan<byte> meta, ReadOnlySpan<byte> itemType)
    {
        // meta 是 FullBox：先跳过 version 与 flags
        if (meta.Length < 4)
        {
            return false;
        }

        var iinf = FindChild(meta[4..], "iinf"u8);
        var entryCountLength = iinf.Length > 0 && iinf[0] == 0 ? 2 : 4;
        if (iinf.Length < 4 + entryCountLength)
        {
            return false;
        }

        var children = iinf[(4 + entryCountLength)..];
        var position = 0;
        while (NextBox(children, ref position) is { } box)
        {
            var content = children[box.ContentStart..box.End];
            if (!children.Slice(box.TypeOffset, 4).SequenceEqual("infe"u8) || content.Length < 4)
            {
                continue;
            }

            var version = content[0];
            if (version < 2)
            {
                continue;
            }

            // version 2 的 item_ID 为 16 位，version 3 为 32 位；之后是 16 位 protection_index 与 4 字节类型
            var typeOffset = 4 + (version == 2 ? 2 : 4) + 2;
            if (content.Length >= typeOffset + 4 && content.Slice(typeOffset, 4).SequenceEqual(itemType))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<byte> FindChild(ReadOnlySpan<byte> boxes, ReadOnlySpan<byte> wanted)
    {
        var position = 0;
        while (NextBox(boxes, ref position) is { } box)
        {
            if (boxes.Slice(box.TypeOffset, 4).SequenceEqual(wanted))
            {
                return boxes[box.ContentStart..box.End];
            }
        }

        return default;
    }

    /// <summary>
    /// 读取 <paramref name="position"/> 处的 box 并前移到下一个 box；越界或畸形时返回 <c>null</c>。
    /// </summary>
    private static BoxRange? NextBox(ReadOnlySpan<byte> boxes, ref int position)
    {
        var remaining = boxes[position..];
        if (remaining.Length < 8)
        {
            return null;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        var headerLength = 8;
        if (size == 1)
        {
            if (remaining.Length < 16)
            {
                return null;
            }

            size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(remaining[8..]), long.MaxValue);
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = remaining.Length;
        }

        if (size < headerLength || size > remaining.Length)
        {
            return null;
        }

        var box = new BoxRange(position + 4, position + headerLength, position + (int)size);
        position = box.End;
        return box;
    }

    private readonly record struct BoxRange(int TypeOffset, int ContentStart, int End);
}
