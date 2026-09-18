using System.Buffers.Binary;
using System.IO;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 图片分辨率尺寸元数据（包含物理宽高与方向矫正）
/// </summary>
public readonly record struct ImageDimensions(int Width, int Height)
{
    public double AspectRatio => Height > 0 ? (double)Width / Height : 4.0 / 3.0;
}

/// <summary>
/// 零分配、超高速图片头部尺寸嗅探器
/// </summary>
/// <remarks>
/// 仅读取文件头部几百字节至数 KB 数据，不将整图像素解压至堆内存。
/// 支持从 JPEG (SOF + Exif Orientation)、PNG (IHDR)、HEIC/HEIF/AVIF (ISOBMFF ispe + irot) 中瞬间提取真实显示尺寸。
/// </remarks>
public static class FastImageHeaderReader
{
    private const int MaxScanBytes = 512 * 1024; // 最多扫描前 512KB

    /// <summary>
    /// 尝试读取指定图片文件的真实显示宽高
    /// </summary>
    public static bool TryReadDimensions(string filePath, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string ext = Path.GetExtension(filePath);
            return TryReadDimensions(stream, ext, out dimensions);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试从流中读取真实显示宽高
    /// </summary>
    public static bool TryReadDimensions(Stream stream, string extension, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!stream.CanRead)
        {
            return false;
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadPng(stream, out dimensions);
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadJpeg(stream, out dimensions);
        }

        if (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".heif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".avif", StringComparison.OrdinalIgnoreCase))
        {
            return TryReadHeic(stream, out dimensions);
        }

        // 扩展名未识别时，根据文件头魔数尝试探测
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        if (read < 4) return false;
        stream.Seek(-read, SeekOrigin.Current);

        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            return TryReadPng(stream, out dimensions);
        }

        if (header[0] == 0xFF && header[1] == 0xD8)
        {
            return TryReadJpeg(stream, out dimensions);
        }

        if (read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            return TryReadHeic(stream, out dimensions);
        }

        return false;
    }

    /// <summary>
    /// 读取 PNG (IHDR 块)
    /// </summary>
    private static bool TryReadPng(Stream stream, out ImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> buf = stackalloc byte[24];
        if (stream.Read(buf) < 24) return false;

        // PNG 签名: 89 50 4E 47 0D 0A 1A 0A
        if (buf[0] != 0x89 || buf[1] != 0x50 || buf[2] != 0x4E || buf[3] != 0x47 ||
            buf[4] != 0x0D || buf[5] != 0x0A || buf[6] != 0x1A || buf[7] != 0x0A)
        {
            return false;
        }

        // IHDR 块类型必须为 "IHDR" (49 48 44 52)
        if (buf[12] != 0x49 || buf[13] != 0x48 || buf[14] != 0x44 || buf[15] != 0x52)
        {
            return false;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(20, 4));

        if (width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue)
        {
            dimensions = new ImageDimensions((int)width, (int)height);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读取 JPEG (解析 SOF 标头与 Exif Orientation)
    /// </summary>
    private static bool TryReadJpeg(Stream stream, out ImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> markerBuf = stackalloc byte[2];
        if (stream.Read(markerBuf) < 2 || markerBuf[0] != 0xFF || markerBuf[1] != 0xD8)
        {
            return false;
        }

        int width = 0;
        int height = 0;
        int orientation = 1; // 默认正常方向

        Span<byte> app1Buf = stackalloc byte[4096];
        Span<byte> sofBuf = stackalloc byte[5];
        long startPos = stream.Position;

        while (stream.Position - startPos < MaxScanBytes)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b != 0xFF) continue;

            // 读取非 0xFF 的标记字节
            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0) break;
            } while (marker == 0xFF);

            if (marker < 0) break;

            // SOS (图像数据开始) 或 EOI (结束)
            if (marker == 0xDA || marker == 0xD9)
            {
                break;
            }

            // 无负载标记
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            // 读取段长度 (大端 16 位，包含这 2 字节本身)
            if (stream.Read(markerBuf) < 2) break;
            ushort segLen = BinaryPrimitives.ReadUInt16BigEndian(markerBuf);
            if (segLen < 2) break;
            int payloadLen = segLen - 2;

            // APP1 (Exif) - 查找旋转方向
            if (marker == 0xE1 && orientation == 1 && payloadLen >= 14)
            {
                int readLen = Math.Min(payloadLen, app1Buf.Length);
                var slice = app1Buf.Slice(0, readLen);
                if (stream.Read(slice) == readLen)
                {
                    orientation = ParseJpegExifOrientation(slice);
                }
                int remaining = payloadLen - readLen;
                if (remaining > 0)
                {
                    stream.Seek(remaining, SeekOrigin.Current);
                }
                continue;
            }

            // SOF 标头 (SOF0 ~ SOF15，除 DHT/JPG/DAC 外)
            bool isSof = (marker >= 0xC0 && marker <= 0xC3) ||
                         (marker >= 0xC5 && marker <= 0xC7) ||
                         (marker >= 0xC9 && marker <= 0xCB) ||
                         (marker >= 0xCD && marker <= 0xCF);

            if (isSof && payloadLen >= 5)
            {
                if (stream.Read(sofBuf) == 5)
                {
                    // sofBuf: [0]=精度, [1..2]=高度, [3..4]=宽度
                    height = BinaryPrimitives.ReadUInt16BigEndian(sofBuf.Slice(1, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(sofBuf.Slice(3, 2));
                }
                break;
            }

            // 跳过其他段负载
            stream.Seek(payloadLen, SeekOrigin.Current);
        }

        if (width > 0 && height > 0)
        {
            // Exif Orientation: 5, 6, 7, 8 表示旋转 90 或 270 度（宽高互换）
            if (orientation is >= 5 and <= 8)
            {
                (width, height) = (height, width);
            }
            dimensions = new ImageDimensions(width, height);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析 JPEG APP1 数据中的 Exif Orientation 标签
    /// </summary>
    private static int ParseJpegExifOrientation(ReadOnlySpan<byte> data)
    {
        // 需满足 "Exif\0\0"
        if (data.Length < 14) return 1;
        if (data[0] != (byte)'E' || data[1] != (byte)'x' || data[2] != (byte)'i' || data[3] != (byte)'f' ||
            data[4] != 0 || data[5] != 0)
        {
            return 1;
        }

        var tiff = data[6..];
        if (tiff.Length < 8) return 1;

        bool isLittleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49) // "II"
        {
            isLittleEndian = true;
        }
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D) // "MM"
        {
            isLittleEndian = false;
        }
        else
        {
            return 1;
        }

        ushort magic = isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(2, 2));
        if (magic != 0x002A) return 1;

        uint ifdOffset = isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(4, 4));

        if (ifdOffset + 2 > tiff.Length) return 1;

        ushort entriesCount = isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice((int)ifdOffset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice((int)ifdOffset, 2));

        int entryPos = (int)ifdOffset + 2;
        for (int i = 0; i < entriesCount && entryPos + 12 <= tiff.Length; i++, entryPos += 12)
        {
            var entry = tiff.Slice(entryPos, 12);
            ushort tag = isLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(0, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(0, 2));

            // Tag 0x0112 = Orientation
            if (tag == 0x0112)
            {
                ushort val = isLittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(8, 2))
                    : BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(8, 2));
                return val is >= 1 and <= 8 ? val : 1;
            }
        }

        return 1;
    }

    /// <summary>
    /// 读取 HEIC / HEIF / AVIF (基于 ISOBMFF ispe 与 irot)
    /// </summary>
    private static bool TryReadHeic(Stream stream, out ImageDimensions dimensions)
    {
        dimensions = default;
        Span<byte> boxHeader = stackalloc byte[8];

        int bestWidth = 0;
        int bestHeight = 0;
        int rotationAngle = 0; // 0, 90, 180, 270

        long streamLen = stream.CanSeek ? stream.Length : long.MaxValue;

        Span<byte> size64Buf = stackalloc byte[8];

        while (stream.Position + 8 <= streamLen && stream.Position < MaxScanBytes)
        {
            if (stream.Read(boxHeader) < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(boxHeader.Slice(0, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(boxHeader.Slice(4, 4));

            long payloadSize;
            if (size32 == 1) // 64位扩展尺寸
            {
                if (stream.Read(size64Buf) < 8) break;
                payloadSize = (long)BinaryPrimitives.ReadUInt64BigEndian(size64Buf) - 16;
            }
            else if (size32 == 0) // 到文件尾部
            {
                payloadSize = streamLen - stream.Position;
            }
            else
            {
                payloadSize = size32 - 8;
            }

            if (payloadSize < 0) break;

            // "meta" box (0x6D657461)
            if (type == 0x6D657461)
            {
                // meta 是 FullBox，跳过 4 字节 version + flags
                if (payloadSize < 4) break;
                stream.Seek(4, SeekOrigin.Current);
                long metaEnd = stream.Position + payloadSize - 4;

                ParseIsobiffContainer(stream, metaEnd, ref bestWidth, ref bestHeight, ref rotationAngle);
                break;
            }

            // 跳过其他非 meta 根 box
            stream.Seek(payloadSize, SeekOrigin.Current);
        }

        if (bestWidth > 0 && bestHeight > 0)
        {
            if (rotationAngle is 90 or 270)
            {
                (bestWidth, bestHeight) = (bestHeight, bestWidth);
            }
            dimensions = new ImageDimensions(bestWidth, bestHeight);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 递归扫描 ISOBMFF 容器查找 iprp -> ipco -> ispe / irot
    /// </summary>
    private static void ParseIsobiffContainer(Stream stream, long endPos, ref int bestWidth, ref int bestHeight, ref int rotationAngle)
    {
        Span<byte> subHeader = stackalloc byte[8];
        Span<byte> size64Buf = stackalloc byte[8];
        Span<byte> ispeData = stackalloc byte[12];

        while (stream.Position + 8 <= endPos)
        {
            if (stream.Read(subHeader) < 8) break;

            uint size = BinaryPrimitives.ReadUInt32BigEndian(subHeader.Slice(0, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(subHeader.Slice(4, 4));

            long boxEnd;
            if (size == 1)
            {
                if (stream.Read(size64Buf) < 8) break;
                boxEnd = stream.Position - 8 + (long)BinaryPrimitives.ReadUInt64BigEndian(size64Buf) - 8;
            }
            else
            {
                boxEnd = stream.Position + size - 8;
            }

            if (boxEnd > endPos || size < 8)
            {
                break;
            }

            // "iprp" (0x69707270) 或 "ipco" (0x6970636F)
            if (type is 0x69707270 or 0x6970636F)
            {
                ParseIsobiffContainer(stream, boxEnd, ref bestWidth, ref bestHeight, ref rotationAngle);
            }
            // "ispe" (Image Spatial Extent, 0x69737065)
            else if (type == 0x69737065 && boxEnd - stream.Position >= 12)
            {
                // FullBox: 4 字节 version+flags + 4 字节 width + 4 字节 height
                if (stream.Read(ispeData) == 12)
                {
                    uint w = BinaryPrimitives.ReadUInt32BigEndian(ispeData.Slice(4, 4));
                    uint h = BinaryPrimitives.ReadUInt32BigEndian(ispeData.Slice(8, 4));

                    // 取最大尺寸的 ispe（避免取到微缩略图）
                    if (w * (long)h > bestWidth * (long)bestHeight)
                    {
                        bestWidth = (int)w;
                        bestHeight = (int)h;
                    }
                }
            }
            // "irot" (Image Rotation, 0x69726F74)
            else if (type == 0x69726F74 && boxEnd - stream.Position >= 1)
            {
                int rotByte = stream.ReadByte();
                if (rotByte >= 0)
                {
                    rotationAngle = (rotByte & 3) * 90;
                }
            }

            stream.Seek(boxEnd, SeekOrigin.Begin);
        }
    }
}
