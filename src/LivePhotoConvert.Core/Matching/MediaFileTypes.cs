using System.Collections.Frozen;

namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 参与合成与拆分的媒体文件类型定义与基于 ISOBMFF 二进制特征的高性能格式嗅探器
/// </summary>
public static class MediaFileTypes
{
    /// <summary>
    /// 可作为动态照片封面的图片扩展名集合（基于 FrozenSet 实现 O(1) 极速检索）
    /// </summary>
    public static readonly FrozenSet<string> PhotoExtensions =
        new[] { ".jpg", ".jpeg", ".heic", ".png" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 可作为动态照片内嵌视频的扩展名集合（基于 FrozenSet 实现 O(1) 极速检索）
    /// </summary>
    public static readonly FrozenSet<string> VideoExtensions =
        new[] { ".mov", ".mp4", ".avi", ".mkv", ".flv" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 同名多格式图片的取用优先级数组（越靠前优先级越高）
    /// </summary>
    /// <remarks>HEIC 是 iPhone 的原始高效率图像格式，画质优于同时导出的 JPG，因此优先取用。</remarks>
    public static readonly string[] PhotoExtensionPriority = [".heic", ".jpg", ".jpeg", ".png"];

    /// <summary>
    /// 同名多格式视频的取用优先级数组（越靠前优先级越高）
    /// </summary>
    public static readonly string[] VideoExtensionPriority = [".mov", ".mp4", ".avi", ".mkv", ".flv"];

    /// <summary>
    /// 图片扩展名排名字典（提供扩展名到优先级索引的 O(1) 查找）
    /// </summary>
    public static readonly FrozenDictionary<string, int> PhotoExtensionRanks =
        PhotoExtensionPriority.Select((ext, idx) => (ext, idx)).ToFrozenDictionary(x => x.ext, x => x.idx, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 视频扩展名排名字典（提供扩展名到优先级索引的 O(1) 查找）
    /// </summary>
    public static readonly FrozenDictionary<string, int> VideoExtensionRanks =
        VideoExtensionPriority.Select((ext, idx) => (ext, idx)).ToFrozenDictionary(x => x.ext, x => x.idx, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HEIC / HEIF 常见的 FourCC 品牌标识（每 4 字节表示一个 FourCC 标识符）
    /// </summary>
    /// <remarks>
    /// 涵盖：heic (HEVC 单图)、heix (HEVC 扩展序列)、hevc/hevx (HEVC 品牌)、mif1/msf1 (Multi-image)、heis (图像序列)、miaf (多媒体应用)。
    /// </remarks>
    private static ReadOnlySpan<byte> HeicBrands => "heicheixhevchevxmif1msf1heismiaf"u8;

    /// <summary>
    /// AVIF (AV1 Image File Format) 常见的 FourCC 品牌标识
    /// </summary>
    private static ReadOnlySpan<byte> AvifBrands => "avifavis"u8;

    /// <summary>
    /// Apple QuickTime 视频容器专用的 FourCC 品牌标识（注意后两位为空格 'qt  '，共 4 字节）
    /// </summary>
    private static ReadOnlySpan<byte> MovBrands => "qt  "u8;

    /// <summary>
    /// 标准 MP4 容器常见的 FourCC 品牌标识
    /// </summary>
    private static ReadOnlySpan<byte> Mp4Brands => "mp41mp42isomiso2avc1MSNVhevchvc1"u8;

    /// <summary>
    /// 判断指定路径的文件是否属于支持的图片格式
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>若为支持的图片格式返回 <c>true</c>，否则返回 <c>false</c></returns>
    public static bool IsPhoto(string filePath) => PhotoExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// 判断指定路径的文件是否属于支持的视频格式
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>若为支持的视频格式返回 <c>true</c>，否则返回 <c>false</c></returns>
    public static bool IsVideo(string filePath) => VideoExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// 判断文件是否确实为标准 JPEG 图片（结合扩展名与文件头部 3 字节魔数 0xFF 0xD8 0xFF 校验）
    /// </summary>
    /// <remarks>安卓动态照片规范要求封面必须为标准 JPEG，其余格式（如 HEIC/PNG）或被错误重命名的文件都需要转换。</remarks>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否为真实有效的 JPEG 图片</returns>
    public static bool IsJpeg(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[3];
            var read = stream.Read(header);
            return read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断文件扩展名是否为 .mp4
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否为 MP4 扩展名</returns>
    public static bool IsMp4(string filePath) => Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 根据文件头部特征字节嗅探图片的真实格式扩展名（基于 ISOBMFF 与魔数，零堆内存分配）
    /// </summary>
    /// <param name="header">文件头部至少 16 字节的数据切片</param>
    /// <param name="fallback">无法识别时的回退扩展名（默认为 .jpg）</param>
    /// <returns>识别出的扩展名（含句点，如 .jpg、.heic、.png、.avif）</returns>
    public static string DetectPhotoExtension(ReadOnlySpan<byte> header, string fallback = ".jpg")
    {
        return header.Length switch
        {
            // JPEG: 0xFF 0xD8 0xFF
            >= 3 when header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF => ".jpg",
            // PNG: 8 字节固定签名 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
            >= 8 when header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A => ".png",
            // HEIC / HEIF (ISOBMFF ftyp box)
            >= 12 when IsFtypBrand(header, HeicBrands) => ".heic",
            // AVIF (ISOBMFF ftyp box)
            >= 12 when IsFtypBrand(header, AvifBrands) => ".avif",
            _ => fallback
        };
    }

    /// <summary>
    /// 根据文件头部特征字节嗅探视频的真实格式扩展名（基于 ISOBMFF FourCC 品牌，零堆内存分配）
    /// </summary>
    /// <param name="header">文件头部至少 16 字节的数据切片</param>
    /// <param name="fallback">无法识别时的回退扩展名（默认为 .mp4）</param>
    /// <returns>识别出的扩展名（含句点，如 .mp4、.mov）</returns>
    public static string DetectVideoExtension(ReadOnlySpan<byte> header, string fallback = ".mp4")
    {
        return header.Length switch
        {
            // Apple QuickTime: ftyp box 中包含 'qt  '
            >= 12 when IsFtypBrand(header, MovBrands) => ".mov",
            // 标准 MP4 容器
            >= 12 when IsFtypBrand(header, Mp4Brands) || IsFtypBox(header) => ".mp4",
            _ => fallback
        };
    }

    /// <summary>
    /// 判断切片是否符合 ISOBMFF 的 ftyp (File Type Box) 特征（偏移 4..7 为 "ftyp" ASCII 码）
    /// </summary>
    /// <param name="header">文件头部切片</param>
    /// <returns>是否为 ftyp box</returns>
    private static bool IsFtypBox(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';

    /// <summary>
    /// 零堆分配判断 ISOBMFF ftyp Box 中的 MajorBrand 或 CompatibleBrands 是否匹配指定的 FourCC 品牌列表
    /// </summary>
    /// <param name="header">文件头部数据切片</param>
    /// <param name="brandsConcatenated">预拼接的 4 字节 FourCC 品牌序列（如 "heicheix..."u8）</param>
    /// <returns>若命中任何一个品牌则返回 <c>true</c></returns>
    private static bool IsFtypBrand(ReadOnlySpan<byte> header, ReadOnlySpan<byte> brandsConcatenated)
    {
        if (!IsFtypBox(header))
        {
            return false;
        }

        // 1. 检查主品牌 Major Brand (偏移 8..11，共 4 字节)
        if (header.Length >= 12 && ContainsFourCc(brandsConcatenated, header.Slice(8, 4)))
        {
            return true;
        }

        // 2. 检查兼容品牌列表 Compatible Brands (偏移 16 起，每 4 字节一组)
        for (var i = 16; i + 4 <= header.Length; i += 4)
        {
            if (ContainsFourCc(brandsConcatenated, header.Slice(i, 4)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在 4 字节对齐的 FourCC 连接串中查找是否包含目标 4 字节标识
    /// </summary>
    /// <param name="brandsConcatenated">4 字节对齐连接的 FourCC 品牌集合</param>
    /// <param name="target">待匹配的 4 字节目标标识</param>
    /// <returns>是否包含</returns>
    private static bool ContainsFourCc(ReadOnlySpan<byte> brandsConcatenated, ReadOnlySpan<byte> target)
    {
        for (var i = 0; i + 4 <= brandsConcatenated.Length; i += 4)
        {
            if (EqualsIgnoreCaseFourCc(brandsConcatenated.Slice(i, 4), target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 忽略大小写比对两个 4 字节的 ASCII FourCC 标识（利用位运算 (b | 0x20) 将大写 ASCII 快速转为小写）
    /// </summary>
    /// <param name="a">第一个 4 字节切片</param>
    /// <param name="b">第二个 4 字节切片</param>
    /// <returns>是否在忽略大小写下相同</returns>
    private static bool EqualsIgnoreCaseFourCc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return (a[0] | 0x20) == (b[0] | 0x20)
            && (a[1] | 0x20) == (b[1] | 0x20)
            && (a[2] | 0x20) == (b[2] | 0x20)
            && (a[3] | 0x20) == (b[3] | 0x20);
    }
}


