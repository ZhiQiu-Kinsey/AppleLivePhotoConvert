using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// Google Container 目录中的一个媒体项。
/// </summary>
/// <param name="Semantic">Primary / GainMap / MotionPhoto 等语义</param>
/// <param name="Mime">媒体类型</param>
/// <param name="Length">该项在文件中的字节长度（Primary 为 0）</param>
/// <param name="Padding">该项之后的填充字节数</param>
public sealed record ContainerItem(string Semantic, string? Mime, long Length, long Padding)
{
    public bool IsMotionPhoto => Semantic.Equals(MotionPhotoXmp.MotionPhotoSemantic, StringComparison.OrdinalIgnoreCase);

    public bool IsGainMap => Semantic.Equals("GainMap", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 从 XMP 中解析出的动态照片声明。
/// </summary>
public sealed record MotionPhotoXmp
{
    internal const string MotionPhotoSemantic = "MotionPhoto";

    /// <summary>旧版 MicroVideo 规范的视频字节长度（从文件末尾起算）。</summary>
    public long? MicroVideoOffset { get; init; }

    public long? PresentationTimestampUs { get; init; }

    public IReadOnlyList<ContainerItem> Items { get; init; } = [];

    public bool HasGainMap => Items.Any(item => item.IsGainMap);

    /// <summary>
    /// 视频在文件末尾的位置：视频长度，以及视频之后还跟着的字节数（后续媒体项与填充）。
    /// </summary>
    public (long Length, long TrailingBytes)? VideoExtent
    {
        get
        {
            var index = -1;
            for (var i = 0; i < Items.Count; i++)
            {
                if (Items[i].IsMotionPhoto)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0 && Items[index].Length > 0)
            {
                var trailing = Items.Skip(index + 1).Sum(item => item.Length + item.Padding) + Items[index].Padding;
                return (Items[index].Length, trailing);
            }

            return MicroVideoOffset is > 0 ? (MicroVideoOffset.Value, 0) : null;
        }
    }

    /// <summary>
    /// 解析 XMP 包；不是合法 XML 或没有任何动态照片声明时返回 <c>null</c>。
    /// </summary>
    public static MotionPhotoXmp? Parse(string? xmp)
    {
        var document = XmpDocument.TryLoad(xmp);
        if (document is null)
        {
            return null;
        }

        var descriptions = XmpDocument.Descriptions(document).ToList();
        var result = new MotionPhotoXmp
        {
            MicroVideoOffset = ReadLong(descriptions, XmpDocument.GCamera + "MicroVideoOffset"),
            PresentationTimestampUs = ReadLong(descriptions, XmpDocument.GCamera + "MotionPhotoPresentationTimestampUs")
                                      ?? ReadLong(descriptions, XmpDocument.GCamera + "MicroVideoPresentationTimestampUs"),
            Items = [.. XmpDocument.DirectoryItems(document).Select(ToContainerItem)]
        };

        return result.VideoExtent is null && !result.HasGainMap ? null : result;
    }

    /// <summary>
    /// 在已有 XMP 上写入动态照片声明：保留其它命名空间与非视频的目录项（如 Ultra HDR 增益图），把视频项放在目录末尾。
    /// </summary>
    /// <param name="existingXmp">封面原有的 XMP，可为 <c>null</c></param>
    /// <param name="videoLength">追加在文件末尾的视频字节长度</param>
    /// <param name="presentationTimestampUs">封面帧在视频中的时间点（微秒）</param>
    /// <returns>可直接整包写回的 XMP 文本</returns>
    public static string Apply(string? existingXmp, long videoLength, long presentationTimestampUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoLength);
        ArgumentOutOfRangeException.ThrowIfNegative(presentationTimestampUs);

        var document = XmpDocument.TryLoad(existingXmp) ?? XmpDocument.CreateEmpty();
        var keptItems = XmpDocument.DirectoryItems(document)
                                   .Select(ToContainerItem)
                                   .Where(item => !item.IsMotionPhoto)
                                   .ToList();
        RemoveMotionPhotoProperties(document);

        if (keptItems.Count == 0 || !keptItems[0].Semantic.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        {
            keptItems.Insert(0, new ContainerItem("Primary", "image/jpeg", 0, 0));
        }

        keptItems.Add(new ContainerItem(MotionPhotoSemantic, "video/mp4", videoLength, 0));

        var description = XmpDocument.PrimaryDescription(document);
        var timestamp = presentationTimestampUs.ToString(CultureInfo.InvariantCulture);
        XmpDocument.SetProperty(description, "MotionPhoto", "1");
        XmpDocument.SetProperty(description, "MotionPhotoVersion", "1");
        XmpDocument.SetProperty(description, "MotionPhotoPresentationTimestampUs", timestamp);
        XmpDocument.SetProperty(description, "MicroVideo", "1");
        XmpDocument.SetProperty(description, "MicroVideoVersion", "1");
        XmpDocument.SetProperty(description, "MicroVideoOffset", videoLength.ToString(CultureInfo.InvariantCulture));
        XmpDocument.SetProperty(description, "MicroVideoPresentationTimestampUs", timestamp);
        XmpDocument.SetDirectory(description, keptItems);
        return XmpDocument.Serialize(document);
    }

    /// <summary>
    /// 去掉动态照片声明，保留目录中的其它媒体项；原本没有 XMP 时返回 <c>null</c>。
    /// </summary>
    public static string? Remove(string? existingXmp)
    {
        var document = XmpDocument.TryLoad(existingXmp);
        if (document is null)
        {
            return null;
        }

        var keptItems = XmpDocument.DirectoryItems(document)
                                   .Select(ToContainerItem)
                                   .Where(item => !item.IsMotionPhoto)
                                   .ToList();
        RemoveMotionPhotoProperties(document);

        // 只剩 Primary 时目录已无意义；否则保留（例如 Ultra HDR 的增益图项）
        if (keptItems.Count > 1)
        {
            XmpDocument.SetDirectory(XmpDocument.PrimaryDescription(document), keptItems);
        }

        return XmpDocument.Serialize(document);
    }

    private static void RemoveMotionPhotoProperties(XDocument document)
    {
        foreach (var description in XmpDocument.Descriptions(document).ToList())
        {
            foreach (var name in XmpDocument.MotionPhotoPropertyNames)
            {
                XmpDocument.RemoveProperty(description, XmpDocument.GCamera + name);
            }

            XmpDocument.RemoveProperty(description, XmpDocument.Container + "Directory");
        }
    }

    /// <remarks>负的长度与填充不合规范，按 0 处理，避免推算出越过文件末尾的视频位置。</remarks>
    internal static ContainerItem ToContainerItem(XElement item) => new(
        XmpDocument.ReadValue(item, XmpDocument.Item + "Semantic") ?? string.Empty,
        XmpDocument.ReadValue(item, XmpDocument.Item + "Mime"),
        Math.Max(0, ParseLong(XmpDocument.ReadValue(item, XmpDocument.Item + "Length")) ?? 0),
        Math.Max(0, ParseLong(XmpDocument.ReadValue(item, XmpDocument.Item + "Padding")) ?? 0));

    private static long? ReadLong(IEnumerable<XElement> descriptions, XName name) =>
        descriptions.Select(description => ParseLong(XmpDocument.ReadValue(description, name))).FirstOrDefault(value => value.HasValue);

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// XMP 的 RDF 结构读写：同一属性既可能写成 rdf:Description 的特性，也可能写成子元素。
/// </summary>
internal static class XmpDocument
{
    public static readonly XNamespace Meta = "adobe:ns:meta/";
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace GCamera = "http://ns.google.com/photos/1.0/camera/";
    public static readonly XNamespace Container = "http://ns.google.com/photos/1.0/container/";
    public static readonly XNamespace Item = "http://ns.google.com/photos/1.0/container/item/";

    public static readonly string[] MotionPhotoPropertyNames =
    [
        "MotionPhoto", "MotionPhotoVersion", "MotionPhotoPresentationTimestampUs",
        "MicroVideo", "MicroVideoVersion", "MicroVideoOffset", "MicroVideoPresentationTimestampUs"
    ];

    public static XDocument? TryLoad(string? xmp)
    {
        if (string.IsNullOrWhiteSpace(xmp))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xmp.TrimStart('\uFEFF').Trim('\0'), LoadOptions.PreserveWhitespace);
            return document.Descendants(Rdf + "RDF").Any() ? document : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static XDocument CreateEmpty() => new(
        new XElement(Meta + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", Meta),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty)))));

    public static IEnumerable<XElement> Descriptions(XDocument document) =>
        document.Descendants(Rdf + "RDF").Elements(Rdf + "Description");

    public static XElement PrimaryDescription(XDocument document)
    {
        var description = Descriptions(document).FirstOrDefault();
        if (description is not null)
        {
            return description;
        }

        description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
        document.Descendants(Rdf + "RDF").First().Add(description);
        return description;
    }

    public static string? ReadValue(XElement owner, XName name) =>
        owner.Attribute(name)?.Value
        ?? owner.Element(name)?.Value
        ?? owner.Elements(Rdf + "Description").Select(child => ReadValue(child, name)).FirstOrDefault(value => value is not null);

    public static void RemoveProperty(XElement owner, XName name)
    {
        owner.Attribute(name)?.Remove();
        owner.Elements(name).Remove();
    }

    public static void SetProperty(XElement description, string name, string value)
    {
        EnsurePrefix(description, "GCamera", GCamera);
        description.SetAttributeValue(GCamera + name, value);
    }

    /// <summary>
    /// 读取 Container:Directory 下每个 rdf:li 对应的 Container:Item 节点。
    /// </summary>
    public static IEnumerable<XElement> DirectoryItems(XDocument document) =>
        document.Descendants(Container + "Directory")
                .Take(1)
                .SelectMany(directory => directory.Descendants(Rdf + "li"))
                .Select(li => li.Descendants(Container + "Item").FirstOrDefault() ?? li);

    public static void SetDirectory(XElement description, IReadOnlyList<ContainerItem> items)
    {
        EnsurePrefix(description, "Container", Container);
        EnsurePrefix(description, "Item", Item);
        var sequence = new XElement(Rdf + "Seq");
        foreach (var item in items)
        {
            var node = new XElement(Container + "Item", new XAttribute(Item + "Semantic", item.Semantic));
            if (item.Mime is not null)
            {
                node.Add(new XAttribute(Item + "Mime", item.Mime));
            }

            if (item.Length > 0)
            {
                node.Add(new XAttribute(Item + "Length", item.Length.ToString(CultureInfo.InvariantCulture)));
            }

            if (item.Padding > 0 || item.Semantic.Equals(MotionPhotoXmp.MotionPhotoSemantic, StringComparison.OrdinalIgnoreCase))
            {
                node.Add(new XAttribute(Item + "Padding", item.Padding.ToString(CultureInfo.InvariantCulture)));
            }

            sequence.Add(new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"), node));
        }

        description.Add(new XElement(Container + "Directory", sequence));
    }

    public static string Serialize(XDocument document) =>
        (document.Root ?? throw new InvalidOperationException("XMP 缺少根元素。")).ToString(SaveOptions.DisableFormatting);

    public static void EnsurePrefix(XElement element, string prefix, XNamespace ns)
    {
        if (element.GetPrefixOfNamespace(ns) is null)
        {
            element.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        }
    }
}
