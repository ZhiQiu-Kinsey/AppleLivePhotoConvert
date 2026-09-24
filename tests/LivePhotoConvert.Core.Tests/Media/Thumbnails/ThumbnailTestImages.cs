using System.Buffers.Binary;
using ImageMagick;
using LivePhotoConvert.Core.Media.Thumbnails;

namespace LivePhotoConvert.Core.Tests.Media.Thumbnails;

/// <summary>
/// 构造带指定 EXIF 方向、内嵌缩略图与 ICC 的真实 JPEG。
/// EXIF 段手工拼接：Magick 写 JPEG 时会按图像自身属性改写方向且不写 IFD1 缩略图。
/// </summary>
internal static class ThumbnailTestImages
{
    public static byte[] Jpeg(int width, int height, MagickColor color, IColorProfile? profile = null)
    {
        using var image = new MagickImage(color, (uint)width, (uint)height);
        if (profile is not null)
        {
            image.SetProfile(profile);
        }

        image.Format = MagickFormat.Jpeg;
        image.Quality = 95;
        return image.ToByteArray();
    }

    /// <summary>在 SOI 之后插入 APP1 Exif：IFD0 只含 Orientation，可选 IFD1 JPEG 缩略图。</summary>
    public static byte[] WithExif(byte[] jpeg, ushort orientation, byte[]? thumbnail = null)
    {
        const int entry = 12;
        var ifd0Size = 2 + entry + 4;
        var ifd1Size = thumbnail is null ? 0 : 2 + 3 * entry + 4;
        var dataOffset = 8 + ifd0Size + ifd1Size;
        var tiff = new byte[dataOffset + (thumbnail?.Length ?? 0)];
        var s = tiff.AsSpan();

        "II"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 8);

        var p = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(s[p..], 1);
        p += 2;
        WriteEntry(s, ref p, 0x0112, 3, orientation);
        BinaryPrimitives.WriteUInt32LittleEndian(s[p..], thumbnail is null ? 0u : (uint)(8 + ifd0Size));
        p += 4;

        if (thumbnail is not null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[p..], 3);
            p += 2;
            WriteEntry(s, ref p, 0x0103, 3, 6);
            WriteEntry(s, ref p, 0x0201, 4, (uint)dataOffset);
            WriteEntry(s, ref p, 0x0202, 4, (uint)thumbnail.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(s[p..], 0);
            thumbnail.CopyTo(s[dataOffset..]);
        }

        var segment = new byte[10 + tiff.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), checked((ushort)(8 + tiff.Length)));
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment, 10);
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    public static MagickImage Read(ThumbnailResult result)
    {
        using var stream = result.OpenRead();
        return new MagickImage(stream);
    }

    public static (byte R, byte G, byte B) PixelAt(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        // 纯灰内容会被写成单通道 JPEG，按颜色而不是通道下标读取
        var color = pixels.GetPixel(x, y).ToColor()!;
        return (color.R, color.G, color.B);
    }

    private static void WriteEntry(Span<byte> s, ref int p, ushort tag, ushort type, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s[p..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(p + 2)..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(p + 4)..], 1);
        if (type == 3)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[(p + 8)..], (ushort)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[(p + 8)..], value);
        }

        p += 12;
    }
}

/// <summary>可手动推进的时钟。</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
