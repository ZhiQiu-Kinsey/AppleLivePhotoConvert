using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 动态照片内嵌视频的位置。视频之前的字节（封面及可能的增益图）即为静态照片部分。
/// </summary>
/// <param name="Offset">视频起始偏移，同时也是照片部分的长度</param>
/// <param name="Length">视频字节长度</param>
public sealed record EmbeddedVideo(long Offset, long Length);

/// <summary>
/// 图片的结构信息。
/// </summary>
/// <param name="Video">内嵌视频；普通照片为 <c>null</c></param>
/// <param name="HasGainMap">是否带 Ultra HDR 增益图（转码为 HEIC 会丢失 HDR 信息）</param>
public readonly record struct ImageLayout(EmbeddedVideo? Video, bool HasGainMap);

/// <summary>
/// 定位动态照片中内嵌的视频。每个候选位置都必须以 ftyp box 开头才会被采用，
/// 元数据过期（照片被编辑过）或只带增益图的 Ultra HDR 照片不会被误判为动态照片。
/// </summary>
public static class MotionPhotoLayout
{
    private const int MaxJpegHeaderScan = 4 * 1024 * 1024;

    private static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>
    /// 分析图片结构；JPEG 直接解析 XMP，其它格式需由调用方提供 XMP。
    /// </summary>
    /// <param name="path">图片路径</param>
    /// <param name="xmp">已读取的 XMP；为 <c>null</c> 且文件是 JPEG 时自动读取</param>
    public static ImageLayout Inspect(string path, string? xmp = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return Inspect(stream, xmp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static ImageLayout Inspect(Stream stream, string? xmp = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        xmp ??= ReadJpegXmp(stream);
        var declaration = MotionPhotoXmp.Parse(xmp);
        var video = LocateFromXmp(stream, declaration) ?? LocateSamsungTrailer(stream);
        return new ImageLayout(video, declaration?.HasGainMap ?? false);
    }

    /// <summary>
    /// 定位内嵌视频；普通照片返回 <c>null</c>。
    /// </summary>
    public static EmbeddedVideo? Locate(string path, string? xmp = null) => Inspect(path, xmp).Video;

    /// <summary>
    /// 读取 JPEG 的标准 XMP 包（APP1 段）；不是 JPEG 或没有 XMP 时返回 <c>null</c>。
    /// </summary>
    public static string? ReadJpegXmp(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        Span<byte> marker = stackalloc byte[4];
        if (stream.ReadAtLeast(marker[..2], 2, throwOnEndOfStream: false) < 2 || marker[0] != 0xFF || marker[1] != 0xD8)
        {
            return null;
        }

        while (stream.Position < MaxJpegHeaderScan && stream.ReadAtLeast(marker[..2], 2, throwOnEndOfStream: false) == 2)
        {
            if (marker[0] != 0xFF)
            {
                return null;
            }

            var code = marker[1];
            if (code == 0xFF)
            {
                // 填充字节：回退一位继续找标记
                stream.Position -= 1;
                continue;
            }

            if (code is 0xD9 or 0xDA)
            {
                return null;
            }

            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (stream.ReadAtLeast(marker[2..4], 2, throwOnEndOfStream: false) < 2)
            {
                return null;
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(marker[2..4]) - 2;
            if (payloadLength < 0)
            {
                return null;
            }

            if (code == 0xE1 && payloadLength > XmpSignature.Length)
            {
                var payload = new byte[payloadLength];
                if (stream.ReadAtLeast(payload, payloadLength, throwOnEndOfStream: false) < payloadLength)
                {
                    return null;
                }

                if (payload.AsSpan().StartsWith(XmpSignature))
                {
                    return Encoding.UTF8.GetString(payload.AsSpan(XmpSignature.Length));
                }

                continue;
            }

            stream.Seek(payloadLength, SeekOrigin.Current);
        }

        return null;
    }

    /// <summary>
    /// 判断指定偏移处是否是 MP4/MOV 容器头。
    /// </summary>
    public static bool IsVideoAt(Stream stream, long offset)
    {
        if (offset <= 0 || offset + 12 > stream.Length)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        stream.Position = offset;
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
               && MediaFileTypes.IsValidVideoPayload(header);
    }

    private static EmbeddedVideo? LocateFromXmp(Stream stream, MotionPhotoXmp? declaration)
    {
        if (declaration?.VideoExtent is not { } extent)
        {
            return null;
        }

        var offset = stream.Length - extent.TrailingBytes - extent.Length;
        if (IsVideoAt(stream, offset))
        {
            return new EmbeddedVideo(offset, extent.Length);
        }

        // HEIC 动态照片把视频放在 mpvd box 中，声明的长度可能包含 8 字节的 box 头
        if (offset > 0 && IsBoxAt(stream, offset, "mpvd"u8) && IsVideoAt(stream, offset + 8))
        {
            return new EmbeddedVideo(offset + 8, extent.Length - 8);
        }

        return null;
    }

    /// <summary>
    /// 三星相机把视频放在文件尾部的 SEF 容器中（条目名 MotionPhoto_Data），部分机型不写 XMP。
    /// 尾部结构：... SEFH 目录 | 目录长度 (int32 LE) | "SEFT"；目录条目记录各数据块相对目录起点的反向偏移。
    /// </summary>
    private static EmbeddedVideo? LocateSamsungTrailer(Stream stream)
    {
        var length = stream.Length;
        if (length < 32)
        {
            return null;
        }

        Span<byte> tail = stackalloc byte[8];
        stream.Position = length - 8;
        if (stream.ReadAtLeast(tail, 8, throwOnEndOfStream: false) < 8 || !tail[4..].SequenceEqual("SEFT"u8))
        {
            return null;
        }

        var directoryLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
        var directoryStart = length - 8 - directoryLength;
        if (directoryLength < 12 || directoryStart <= 0 || directoryLength > 1024 * 1024)
        {
            return null;
        }

        var directory = new byte[directoryLength];
        stream.Position = directoryStart;
        if (stream.ReadAtLeast(directory, directoryLength, throwOnEndOfStream: false) < directoryLength || !directory.AsSpan(0, 4).SequenceEqual("SEFH"u8))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(8));
        Span<byte> blockHeader = stackalloc byte[8];
        Span<byte> nameBuffer = stackalloc byte[64];
        for (var i = 0; i < count && 12 + (i + 1) * 12 <= directoryLength; i++)
        {
            var entry = directory.AsSpan(12 + i * 12, 12);
            var blockStart = directoryStart - BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            if (blockStart <= 0 || blockStart + blockSize > directoryStart)
            {
                continue;
            }

            stream.Position = blockStart;
            if (stream.ReadAtLeast(blockHeader, 8, throwOnEndOfStream: false) < 8)
            {
                continue;
            }

            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[4..]);
            if (nameLength is <= 0 or > 64)
            {
                continue;
            }

            var name = nameBuffer[..nameLength];
            if (stream.ReadAtLeast(name, nameLength, throwOnEndOfStream: false) < nameLength || !name.SequenceEqual("MotionPhoto_Data"u8))
            {
                continue;
            }

            var videoOffset = blockStart + 8 + nameLength;
            var videoLength = blockSize - 8 - nameLength;
            if (videoLength > 0 && IsVideoAt(stream, videoOffset))
            {
                return new EmbeddedVideo(videoOffset, videoLength);
            }
        }

        return null;
    }

    private static bool IsBoxAt(Stream stream, long offset, ReadOnlySpan<byte> type)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position = offset;
        return stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) == 8 && header[4..].SequenceEqual(type);
    }
}
