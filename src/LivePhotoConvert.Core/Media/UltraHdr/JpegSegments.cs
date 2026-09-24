using System.Buffers.Binary;

namespace LivePhotoConvert.Core.Media.UltraHdr;

/// <summary>
/// JPEG 头部的一个标记段。
/// </summary>
/// <param name="Marker">标记码（0xE0～0xEF 为 APPn，0xFE 为 COM）</param>
/// <param name="Offset">段起点（0xFF 所在位置）</param>
/// <param name="PayloadLength">段内容长度（不含标记与长度字段）</param>
internal readonly record struct JpegSegment(byte Marker, long Offset, int PayloadLength)
{
    public long PayloadOffset => Offset + 4;

    public long End => PayloadOffset + PayloadLength;
}

/// <summary>
/// 读取 JPEG 头部的 APPn / COM 段；遇到 DQT、SOF、SOS 等图像数据相关的段即停止。
/// </summary>
internal static class JpegSegments
{
    public static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/\0"u8;

    public static ReadOnlySpan<byte> XmpExtensionSignature => "http://ns.adobe.com/xmp/extension/\0"u8;

    public static ReadOnlySpan<byte> ExifSignature => "Exif\0\0"u8;

    public static ReadOnlySpan<byte> IccSignature => "ICC_PROFILE\0"u8;

    public static ReadOnlySpan<byte> MpfSignature => "MPF\0"u8;

    /// <summary>单段内容上限（长度字段 16 位，含自身 2 字节）。</summary>
    public const int MaxPayloadLength = ushort.MaxValue - 2;

    private const int MaxSegments = 4096;

    /// <summary>
    /// 从 <paramref name="start"/> 处的 SOI 开始读取头部段。
    /// </summary>
    /// <param name="stream">可定位的流</param>
    /// <param name="start">SOI 的位置</param>
    /// <param name="bodyOffset">第一个非 APPn / COM 段的位置，之后是量化表、帧头与熵编码数据</param>
    /// <returns>不是 JPEG 或结构损坏时返回 <c>null</c></returns>
    public static List<JpegSegment>? ReadHeader(Stream stream, long start, out long bodyOffset)
    {
        bodyOffset = 0;
        Span<byte> buffer = stackalloc byte[4];
        stream.Position = start;
        if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2 || buffer[0] != 0xFF || buffer[1] != 0xD8)
        {
            return null;
        }

        var segments = new List<JpegSegment>();
        while (segments.Count < MaxSegments)
        {
            var position = stream.Position;
            if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2 || buffer[0] != 0xFF)
            {
                return null;
            }

            var marker = buffer[1];
            if (marker == 0xFF)
            {
                // 段之间允许任意个填充字节 0xFF
                stream.Position = position + 1;
                continue;
            }

            if (marker is not ((>= 0xE0 and <= 0xEF) or 0xFE))
            {
                bodyOffset = position;
                return segments;
            }

            if (stream.ReadAtLeast(buffer[2..4], 2, throwOnEndOfStream: false) < 2)
            {
                return null;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..4]) - 2;
            if (payloadLength < 0 || position + 4 + payloadLength > stream.Length)
            {
                return null;
            }

            segments.Add(new JpegSegment(marker, position, payloadLength));
            stream.Position = position + 4 + payloadLength;
        }

        return null;
    }

    public static bool StartsWith(Stream stream, JpegSegment segment, ReadOnlySpan<byte> signature)
    {
        if (segment.PayloadLength < signature.Length)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[signature.Length];
        stream.Position = segment.PayloadOffset;
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.SequenceEqual(signature);
    }

    public static byte[] ReadSegment(Stream stream, JpegSegment segment)
    {
        var bytes = new byte[segment.PayloadLength + 4];
        stream.Position = segment.Offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static byte[] ReadPayload(Stream stream, JpegSegment segment)
    {
        var bytes = new byte[segment.PayloadLength];
        stream.Position = segment.PayloadOffset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// 组装一个完整的段：标记、长度、签名与内容。
    /// </summary>
    public static byte[] Build(byte marker, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> content)
    {
        var payloadLength = signature.Length + content.Length;
        if (payloadLength > MaxPayloadLength)
        {
            throw new InvalidDataException($"JPEG 段内容过长：{payloadLength} 字节，上限 {MaxPayloadLength}。");
        }

        var bytes = new byte[payloadLength + 4];
        bytes[0] = 0xFF;
        bytes[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)(payloadLength + 2));
        signature.CopyTo(bytes.AsSpan(4));
        content.CopyTo(bytes.AsSpan(4 + signature.Length));
        return bytes;
    }
}
