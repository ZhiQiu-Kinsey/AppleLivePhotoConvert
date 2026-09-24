using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 动态照片内嵌视频的位置。
/// </summary>
/// <param name="Offset">视频数据（ftyp 起）的起始偏移，切出视频或播放时使用</param>
/// <param name="Length">视频数据的字节长度</param>
/// <param name="ImageEnd">静态照片部分（封面及可能的增益图）的结束位置，剥离视频时在此截断。
/// 视频包在容器里时（HEIC 的 mpvd box、三星 SEF 数据块）小于 <paramref name="Offset"/>，否则与其相等。</param>
public sealed record EmbeddedVideo(long Offset, long Length, long ImageEnd)
{
    public EmbeddedVideo(long offset, long length) : this(offset, length, offset)
    {
    }
}

/// <summary>
/// 图片的结构信息。
/// </summary>
/// <param name="Video">内嵌视频；普通照片为 <c>null</c></param>
/// <param name="HasGainMap">是否带 Ultra HDR 增益图（转码为 HEIC 会丢失 HDR 信息）</param>
public readonly record struct ImageLayout(EmbeddedVideo? Video, bool HasGainMap);

/// <summary>
/// 定位动态照片中内嵌的视频。每个候选位置都必须以 ftyp box 开头才会被采用，
/// 元数据过期（照片被编辑过）或只带增益图的 Ultra HDR 照片不会被误判为动态照片。
/// HEIC 动态照片无需 XMP，遍历顶层 box 找到内含 ftyp 的 mpvd 即可识别。
/// </summary>
public static class MotionPhotoLayout
{
    private const int MaxJpegHeaderScan = 4 * 1024 * 1024;

    /// <summary>顶层 box 数量上限；正常 HEIC 只有 ftyp/meta/mdat 等少数几个，防止畸形文件拖慢扫描。</summary>
    private const int MaxTopLevelBoxes = 1024;

    private const uint Ftyp = 0x66747970; // "ftyp"
    private const uint Mpvd = 0x6D707664; // "mpvd"

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
        var video = LocateFromXmp(stream, declaration) ?? LocateMpvdBox(stream) ?? LocateSamsungTrailer(stream);
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
            // 声明的长度不含 mpvd box 头时，偏移直接落在 ftyp 上，box 头仍属于要截掉的部分
            return new EmbeddedVideo(offset, extent.Length, EnclosingMpvdStart(stream, offset) ?? offset);
        }

        // 声明的长度包含 mpvd box 头
        if (offset > 0 && ReadBoxHeader(stream, offset) is { } box && box.Type == Mpvd && IsVideoAt(stream, offset + box.HeaderLength))
        {
            return new EmbeddedVideo(offset + box.HeaderLength, extent.Length - box.HeaderLength, offset);
        }

        return null;
    }

    /// <summary>
    /// 遍历 ISOBMFF（HEIC）的顶层 box，取第一个内容以 ftyp 开头的 mpvd box。
    /// </summary>
    private static EmbeddedVideo? LocateMpvdBox(Stream stream)
    {
        var length = stream.Length;
        if (ReadBoxHeader(stream, 0)?.Type != Ftyp)
        {
            return null;
        }

        long offset = 0;
        for (var count = 0; count < MaxTopLevelBoxes && offset + 8 <= length; count++)
        {
            if (ReadBoxHeader(stream, offset) is not { } box)
            {
                return null;
            }

            // size 为 0 表示延续到文件尾
            var size = box.Size == 0 ? length - offset : box.Size;
            if (size < box.HeaderLength || size > length - offset)
            {
                return null;
            }

            if (box.Type == Mpvd)
            {
                var dataStart = offset + box.HeaderLength;
                return IsVideoAt(stream, dataStart) ? new EmbeddedVideo(dataStart, size - box.HeaderLength, offset) : null;
            }

            offset += size;
        }

        return null;
    }

    /// <summary>
    /// 视频数据紧跟在 mpvd box 头之后时返回 box 起点。
    /// </summary>
    private static long? EnclosingMpvdStart(Stream stream, long videoOffset)
    {
        foreach (var headerLength in (ReadOnlySpan<int>)[8, 16])
        {
            var start = videoOffset - headerLength;
            if (start > 0 && ReadBoxHeader(stream, start) is { } box && box.Type == Mpvd && box.HeaderLength == headerLength)
            {
                return start;
            }
        }

        return null;
    }

    /// <summary>
    /// 读取 box 头：32 位长度 + 类型；长度为 1 时后跟 64 位长度。
    /// </summary>
    private static BoxHeader? ReadBoxHeader(Stream stream, long offset)
    {
        if (offset < 0 || offset + 8 > stream.Length)
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
            return new BoxHeader(size, type, 8);
        }

        if (stream.ReadAtLeast(header[8..], 8, throwOnEndOfStream: false) < 8)
        {
            return null;
        }

        var largeSize = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        return largeSize > long.MaxValue ? null : new BoxHeader((long)largeSize, type, 16);
    }

    private readonly record struct BoxHeader(long Size, uint Type, int HeaderLength);

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
                // 截断时连同数据块头一起去掉
                return new EmbeddedVideo(videoOffset, videoLength, blockStart);
            }
        }

        return null;
    }
}
