using System.Buffers;
using System.Buffers.Binary;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 读取 HEIF 的 item 信息（meta / iinf / infe），不解码图像。
/// </summary>
public static class HeifItems
{
    /// <summary>meta box 通常只有几十 KB，超过上限视为畸形文件。</summary>
    private const int MaxMetaBytes = 16 * 1024 * 1024;

    private const int MaxTopLevelBoxes = 64;

    private const uint BoxIinf = 0x69696E66; // "iinf"
    private const uint BoxInfe = 0x696E6665; // "infe"

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
        if (itemType.Length != 4 || !System.Text.Ascii.IsValid(itemType))
        {
            throw new ArgumentException("item 类型必须是 4 个 ASCII 字符。", nameof(itemType));
        }

        var type = (uint)itemType[0] << 24 | (uint)itemType[1] << 16 | (uint)itemType[2] << 8 | itemType[3];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return FindMeta(stream) is { } meta && ContainsItemType(stream, meta, type);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 在 iinf box 的内容中查找第一个指定类型的 item。只认 infe 版本 2（16 位 ID）与 3（32 位 ID），更早的版本没有 item_type。
    /// </summary>
    /// <param name="iinf">iinf box 的内容（不含 box 头）</param>
    /// <param name="itemType">大端四字符类型</param>
    internal static uint? FindItemId(ReadOnlySpan<byte> iinf, uint itemType)
    {
        // iinf 是 FullBox：版本 0 的条目数为 16 位，否则为 32 位
        var entriesStart = iinf.Length > 0 && iinf[0] == 0 ? 6 : 8;
        if (iinf.Length < entriesStart)
        {
            return null;
        }

        foreach (var (type, infe) in new IsoBoxEnumerator(iinf[entriesStart..]))
        {
            if (type != BoxInfe || infe.Length < 4 || infe[0] is not (2 or 3))
            {
                continue;
            }

            // version/flags | item_ID | protection_index（16 位）| item_type
            var idLength = infe[0] == 2 ? 2 : 4;
            var typeOffset = 4 + idLength + 2;
            if (infe.Length >= typeOffset + 4 && BinaryPrimitives.ReadUInt32BigEndian(infe[typeOffset..]) == itemType)
            {
                return idLength == 2 ? BinaryPrimitives.ReadUInt16BigEndian(infe[4..]) : BinaryPrimitives.ReadUInt32BigEndian(infe[4..]);
            }
        }

        return null;
    }

    /// <summary>
    /// 在顶层 box 中找到 meta，返回其内容的位置与长度（不含 box 头）。
    /// </summary>
    private static (long Offset, int Length)? FindMeta(Stream stream)
    {
        var length = stream.Length;
        long offset = 0;
        for (var count = 0; count < MaxTopLevelBoxes && offset + 8 <= length; count++)
        {
            if (IsoBox.ReadBounded(stream, offset, length) is not { } box || (count == 0 && box.Type != IsoBox.Ftyp))
            {
                return null;
            }

            if (box.Type == IsoBox.Meta)
            {
                var contentLength = box.Size - box.HeaderLength;
                return contentLength <= MaxMetaBytes ? (offset + box.HeaderLength, (int)contentLength) : null;
            }

            offset += box.Size;
        }

        return null;
    }

    private static bool ContainsItemType(Stream stream, (long Offset, int Length) meta, uint itemType)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(meta.Length);
        try
        {
            var content = buffer.AsSpan(0, meta.Length);
            stream.Position = meta.Offset;
            stream.ReadExactly(content);

            // meta 是 FullBox：子 box 从 version/flags 之后开始
            if (content.Length < 4)
            {
                return false;
            }

            foreach (var (type, body) in new IsoBoxEnumerator(content[4..]))
            {
                if (type == BoxIinf)
                {
                    return FindItemId(body, itemType) is not null;
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
