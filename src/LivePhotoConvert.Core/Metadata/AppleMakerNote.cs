using System.Buffers.Binary;
using System.Text;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 生成只含 Apple MakerNotes 的最小 JPEG，作为 ExifTool -tagsFromFile 的来源。
/// </summary>
/// <remarks>
/// iOS 依据照片 MakerNotes 中的 ContentIdentifier（标签 0x0011）与视频配对。ExifTool 不能在没有 Apple MakerNotes
/// 的文件里凭空创建该块，但可以从另一个文件整块复制，因此先构造一个带该块的模板。
/// Apple MakerNotes 结构：<c>"Apple iOS\0" | 版本 0x0001 | "MM" | IFD</c>，IFD 中的偏移相对 MakerNotes 起点。
/// </remarks>
internal static class AppleMakerNote
{
    private const ushort MakerNoteVersionTag = 0x0001;
    private const ushort ContentIdentifierTag = 0x0011;
    private const ushort TypeAscii = 2;
    private const ushort TypeUndefined = 7;
    private const ushort TypeSLong = 9;
    private const ushort TypeLong = 4;
    private const ushort ExifIfdPointerTag = 0x8769;
    private const ushort MakerNoteTag = 0x927C;

    public static byte[] BuildTemplateJpeg(string contentIdentifier)
    {
        var makerNote = BuildMakerNote(contentIdentifier);

        // TIFF：头 8 字节 | IFD0（仅 ExifIFD 指针）| ExifIFD（仅 MakerNote）| MakerNote 数据
        const int ifd0Offset = 8;
        const int ifdSize = 2 + 12 + 4;
        const int exifIfdOffset = ifd0Offset + ifdSize;
        const int makerNoteOffset = exifIfdOffset + ifdSize;
        var tiff = new byte[makerNoteOffset + makerNote.Length];
        var span = tiff.AsSpan();
        "MM"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], ifd0Offset);
        WriteSingleEntryIfd(span[ifd0Offset..], ExifIfdPointerTag, TypeLong, 1, exifIfdOffset);
        WriteSingleEntryIfd(span[exifIfdOffset..], MakerNoteTag, TypeUndefined, (uint)makerNote.Length, makerNoteOffset);
        makerNote.CopyTo(span[makerNoteOffset..]);

        var app1Length = 2 + 6 + tiff.Length;
        var jpeg = new byte[2 + 2 + app1Length + 2];
        var output = jpeg.AsSpan();
        output[0] = 0xFF;
        output[1] = 0xD8;
        output[2] = 0xFF;
        output[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(output[4..], checked((ushort)app1Length));
        "Exif\0\0"u8.CopyTo(output[6..]);
        tiff.CopyTo(output[12..]);
        output[^2] = 0xFF;
        output[^1] = 0xD9;
        return jpeg;
    }

    private static byte[] BuildMakerNote(string contentIdentifier)
    {
        var identifier = Encoding.ASCII.GetBytes(contentIdentifier + "\0");
        ReadOnlySpan<byte> header = "Apple iOS\0\0\u0001MM"u8;
        const int entryCount = 2;
        var ifdSize = 2 + entryCount * 12 + 4;
        var dataOffset = header.Length + ifdSize;
        var buffer = new byte[dataOffset + identifier.Length];
        var span = buffer.AsSpan();
        header.CopyTo(span);

        var ifd = span[header.Length..];
        BinaryPrimitives.WriteUInt16BigEndian(ifd, entryCount);
        WriteEntry(ifd[2..], MakerNoteVersionTag, TypeSLong, 1, 14);
        WriteEntry(ifd[14..], ContentIdentifierTag, TypeAscii, (uint)identifier.Length, (uint)dataOffset);
        identifier.CopyTo(span[dataOffset..]);
        return buffer;
    }

    private static void WriteSingleEntryIfd(Span<byte> ifd, ushort tag, ushort type, uint count, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(ifd, 1);
        WriteEntry(ifd[2..], tag, type, count, (uint)value);
        BinaryPrimitives.WriteUInt32BigEndian(ifd[14..], 0);
    }

    private static void WriteEntry(Span<byte> entry, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(entry, tag);
        BinaryPrimitives.WriteUInt16BigEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], value);
    }
}
