using System.Collections.Frozen;
using System.Text;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 支持的媒体扩展名、同名多格式的取用优先级，以及基于文件头的格式嗅探。
/// </summary>
public static class MediaFileTypes
{
    /// <summary>可作为实况照片封面的图片扩展名，按取用优先级排列（HEIC 是 iPhone 原始格式，优先于同时导出的 JPG）。</summary>
    private static readonly string[] PhotoPriority = [".heic", ".jpg", ".jpeg", ".png"];

    /// <summary>可作为实况视频的扩展名，按取用优先级排列。</summary>
    private static readonly string[] VideoPriority = [".mov", ".mp4", ".avi", ".mkv", ".flv"];

    public static readonly FrozenSet<string> PhotoExtensions = PhotoPriority.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> VideoExtensions = VideoPriority.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>可能内嵌视频的动态照片扩展名。</summary>
    public static readonly FrozenSet<string> MotionPhotoExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".jpg", ".jpeg", ".heic");

    public static readonly FrozenDictionary<string, int> PhotoExtensionRanks = ToRanks(PhotoPriority);

    public static readonly FrozenDictionary<string, int> VideoExtensionRanks = ToRanks(VideoPriority);

    private static ReadOnlySpan<byte> HeicBrands => "heicheixhevchevxmif1msf1heismiaf"u8;

    private static ReadOnlySpan<byte> AvifBrands => "avifavis"u8;

    private static ReadOnlySpan<byte> MovBrands => "qt  "u8;

    private static ReadOnlySpan<byte> Mp4Brands => "mp41mp42isomiso2avc1MSNVhevchvc1"u8;

    public static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsMp4(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeic(string path) => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase);

    public static bool HasJpegExtension(string path) =>
        Path.GetExtension(path) is var ext
        && (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));

    public static int PhotoRank(string path) => PhotoExtensionRanks.GetValueOrDefault(Path.GetExtension(path), int.MaxValue);

    public static int VideoRank(string path) => VideoExtensionRanks.GetValueOrDefault(Path.GetExtension(path), int.MaxValue);

    /// <summary>
    /// 扩展名与文件头同时是 JPEG 才返回 <c>true</c>；被改过扩展名的 HEIC 必须先转码才能作为动态照片封面。
    /// </summary>
    public static bool IsJpeg(string path)
    {
        if (!HasJpegExtension(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[3];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == 3 && IsJpegHeader(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsJpegHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    public static string DetectPhotoExtension(ReadOnlySpan<byte> header, string fallback = ".jpg") => header switch
    {
        _ when IsJpegHeader(header) => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => ".png",
        _ when HasFtypBrand(header, HeicBrands) => ".heic",
        _ when HasFtypBrand(header, AvifBrands) => ".avif",
        _ => fallback
    };

    public static string DetectVideoExtension(ReadOnlySpan<byte> header, string fallback = ".mp4") => header switch
    {
        _ when HasFtypBrand(header, MovBrands) => ".mov",
        _ when IsFtypBox(header) => ".mp4",
        _ => fallback
    };

    /// <summary>判断数据是否以 ISOBMFF 的 ftyp box 开头（MP4 / MOV 的容器头）。</summary>
    public static bool IsValidVideoPayload(ReadOnlySpan<byte> header) => header.Length >= 12 && IsFtypBox(header);

    private static bool IsFtypBox(ReadOnlySpan<byte> header) => header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8);

    /// <summary>主品牌（偏移 8）或兼容品牌（偏移 16 起每 4 字节）命中任一 FourCC 即视为匹配。</summary>
    private static bool HasFtypBrand(ReadOnlySpan<byte> header, ReadOnlySpan<byte> brands)
    {
        if (header.Length < 12 || !IsFtypBox(header))
        {
            return false;
        }

        if (ContainsFourCc(brands, header.Slice(8, 4)))
        {
            return true;
        }

        for (var i = 16; i + 4 <= header.Length; i += 4)
        {
            if (ContainsFourCc(brands, header.Slice(i, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsFourCc(ReadOnlySpan<byte> brands, ReadOnlySpan<byte> target)
    {
        for (var i = 0; i + 4 <= brands.Length; i += 4)
        {
            if (Ascii.EqualsIgnoreCase(brands.Slice(i, 4), target))
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenDictionary<string, int> ToRanks(string[] priority) =>
        priority.Index().ToFrozenDictionary(x => x.Item, x => x.Index, StringComparer.OrdinalIgnoreCase);
}
