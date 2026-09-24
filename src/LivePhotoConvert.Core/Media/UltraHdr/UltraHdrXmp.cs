using System.Xml.Linq;

namespace LivePhotoConvert.Core.Media.UltraHdr;

/// <summary>
/// Ultra HDR 主图的 XMP：hdrgm:Version 与 Container 目录（Primary、GainMap），在已有 XMP 上合并。
/// </summary>
public static class UltraHdrXmp
{
    private static readonly XNamespace Hdrgm = GainMapMetadata.HdrgmNamespace;

    /// <summary>
    /// 声明紧跟主图之后的增益图；保留其它命名空间，目录中原有的非 Primary / GainMap 项（如 MotionPhoto）排在增益图之后。
    /// </summary>
    /// <param name="existingXmp">主图原有的 XMP，可为 <c>null</c></param>
    /// <param name="gainMapLength">增益图 JPEG 的字节长度，必须与 MPF 中记录的一致</param>
    public static string ApplyPrimary(string? existingXmp, long gainMapLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gainMapLength);
        var document = XmpDocument.TryLoad(existingXmp) ?? XmpDocument.CreateEmpty();
        var trailingItems = XmpDocument.DirectoryItems(document)
                                       .Select(MotionPhotoXmp.ToContainerItem)
                                       .Where(item => !item.IsGainMap && !item.Semantic.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                                       .ToList();
        foreach (var description in XmpDocument.Descriptions(document).ToList())
        {
            XmpDocument.RemoveProperty(description, XmpDocument.Container + "Directory");
            XmpDocument.RemoveProperty(description, Hdrgm + "Version");
        }

        var primary = XmpDocument.PrimaryDescription(document);
        XmpDocument.EnsurePrefix(primary, "hdrgm", Hdrgm);
        primary.SetAttributeValue(Hdrgm + "Version", "1.0");
        XmpDocument.SetDirectory(primary,
        [
            new ContainerItem("Primary", "image/jpeg", 0, 0),
            new ContainerItem("GainMap", "image/jpeg", gainMapLength, 0),
            .. trailingItems
        ]);
        return XmpDocument.Serialize(document);
    }
}
