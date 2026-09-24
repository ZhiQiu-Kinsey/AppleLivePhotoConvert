using System.Buffers.Binary;
using System.Text;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 构造结构合法的最小媒体文件：带真实文件头与 XMP 段，足以驱动格式嗅探与视频定位逻辑。
/// </summary>
internal static class SyntheticMedia
{
    private static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>SOI | [APP1 XMP] | 若干 COM 段填充 | EOI</summary>
    public static byte[] Jpeg(int payloadBytes = 2048, string? xmp = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        if (xmp is not null)
        {
            WriteXmpSegment(stream, xmp);
        }

        var remaining = payloadBytes;
        var filler = 0;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, 60_000);
            WriteSegment(stream, 0xFE, Enumerable.Repeat((byte)(filler++ % 251), chunk).ToArray());
            remaining -= chunk;
        }

        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    public static byte[] Mp4(int length = 4096) => IsoFile(length, "isom");

    public static byte[] Mov(int length = 4096) => IsoFile(length, "qt  ");

    public static byte[] Heic(int length = 4096) => IsoFile(length, "heic");

    /// <summary>
    /// Google 规范动态照片：封面写入 Container 目录与 MicroVideoOffset，末尾追加视频。
    /// </summary>
    public static byte[] MotionPhoto(byte[]? cover = null, byte[]? video = null)
    {
        video ??= Mp4();
        cover = InsertXmp(cover ?? Jpeg(), MotionPhotoXmp.Apply(null, video.Length, 1_500_000));
        return [.. cover, .. video];
    }

    /// <summary>
    /// Ultra HDR 风格：主图 + 增益图 + 视频，Container 目录依次声明三项。
    /// </summary>
    public static byte[] MotionPhotoWithGainMap(byte[] gainMap, byte[]? video = null)
    {
        video ??= Mp4();
        var xmp = $"""
                   <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                   <rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0" GCamera:MotionPhoto="1">
                   <Container:Directory><rdf:Seq>
                   <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="image/jpeg" Item:Semantic="Primary"/></rdf:li>
                   <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="image/jpeg" Item:Semantic="GainMap" Item:Length="{gainMap.Length}"/></rdf:li>
                   <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="video/mp4" Item:Semantic="MotionPhoto" Item:Length="{video.Length}" Item:Padding="0"/></rdf:li>
                   </rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>
                   """;
        return [.. Jpeg(xmp: xmp), .. gainMap, .. video];
    }

    /// <summary>
    /// 只带增益图、没有视频的 Ultra HDR 照片。
    /// </summary>
    public static byte[] UltraHdrStill(byte[] gainMap)
    {
        var xmp = $"""
                   <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                   <rdf:Description rdf:about="" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/">
                   <Container:Directory><rdf:Seq>
                   <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="image/jpeg" Item:Semantic="Primary"/></rdf:li>
                   <rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="image/jpeg" Item:Semantic="GainMap" Item:Length="{gainMap.Length}"/></rdf:li>
                   </rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>
                   """;
        return [.. Jpeg(xmp: xmp), .. gainMap];
    }

    /// <summary>
    /// 三星 SEF 尾部格式：视频放在名为 MotionPhoto_Data 的数据块中，文件以 SEFH 目录与 "SEFT" 结尾。
    /// </summary>
    public static byte[] SamsungMotionPhoto(byte[] cover, byte[] video)
    {
        var name = "MotionPhoto_Data"u8.ToArray();
        var block = new byte[8 + name.Length + video.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0x0A30);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), name.Length);
        name.CopyTo(block, 8);
        video.CopyTo(block, 8 + name.Length);

        var directory = new byte[12 + 12];
        "SEFH"u8.CopyTo(directory);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(4), 107);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(14), 0x0A30);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(16), (uint)block.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(20), (uint)block.Length);

        var tail = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(tail, directory.Length);
        "SEFT"u8.CopyTo(tail.AsSpan(4));
        return [.. cover, .. block, .. directory, .. tail];
    }

    /// <summary>
    /// 替换 JPEG 中的 XMP 段；没有 XMP 时插入到 SOI 之后。
    /// </summary>
    public static byte[] InsertXmp(byte[] jpeg, string xmp)
    {
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        WriteXmpSegment(output, xmp);
        var position = 2;
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF && jpeg[position + 1] is not (0xDA or 0xD9))
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(position + 2));
            var isXmp = jpeg[position + 1] == 0xE1 && jpeg.AsSpan(position + 4).StartsWith(XmpSignature);
            if (!isXmp)
            {
                output.Write(jpeg, position, length + 2);
            }

            position += length + 2;
        }

        output.Write(jpeg, position, jpeg.Length - position);
        return output.ToArray();
    }

    private static void WriteXmpSegment(Stream stream, string xmp) =>
        WriteSegment(stream, 0xE1, [.. XmpSignature, .. Encoding.UTF8.GetBytes(xmp)]);

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        Span<byte> header = [0xFF, marker, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], checked((ushort)(payload.Length + 2)));
        stream.Write(header);
        stream.Write(payload);
    }

    private static byte[] IsoFile(int length, string brand)
    {
        var bytes = new byte[Math.Max(length, 32)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 16);
        for (var i = 24; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 199);
        }

        return bytes;
    }
}
