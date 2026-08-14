namespace LivePhotoConvert.Core.Matching;

/// <summary>
/// 参与合成的媒体文件类型定义
/// </summary>
public static class MediaFileTypes
{
    /// <summary>
    /// 可作为动态照片封面的图片扩展名
    /// </summary>
    public static readonly IReadOnlySet<string> PhotoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".png" };

    /// <summary>
    /// 可作为动态照片内嵌视频的扩展名
    /// </summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".avi", ".mkv", ".flv" };

    /// <summary>
    /// 同名图片的取用优先级，越靠前优先级越高
    /// </summary>
    /// <remarks>HEIC 是 iPhone 的原始格式，画质优于同时导出的 JPG，因此优先取用。</remarks>
    public static readonly string[] PhotoExtensionPriority = [".heic", ".jpg", ".jpeg", ".png"];

    /// <summary>
    /// 同名视频的取用优先级，越靠前优先级越高
    /// </summary>
    public static readonly string[] VideoExtensionPriority = [".mov", ".mp4", ".avi", ".mkv", ".flv"];

    /// <summary>
    /// 判断是否为图片
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否为图片</returns>
    public static bool IsPhoto(string filePath) => PhotoExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// 判断是否为视频
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否为视频</returns>
    public static bool IsVideo(string filePath) => VideoExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// 判断文件是否确实为标准 JPEG 图片（结合扩展名与文件头部魔数校验）
    /// </summary>
    /// <remarks>安卓动态照片格式要求封面为 JPEG，其余格式或被错误重命名的图片都需要先转换。</remarks>
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
    /// 判断是否已经是 MP4
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>是否为 MP4</returns>
    public static bool IsMp4(string filePath) => Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 根据文件头部特征字节嗅探图片的真实格式扩展名
    /// </summary>
    /// <param name="header">文件头部至少 16 字节的数据</param>
    /// <param name="fallback">无法识别时的回退扩展名</param>
    /// <returns>扩展名（含句点，如 .jpg、.heic、.png、.avif）</returns>
    public static string DetectPhotoExtension(ReadOnlySpan<byte> header, string fallback = ".jpg")
    {
        return header.Length switch
        {
            >= 3 when header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF => ".jpg",
            >= 8 when header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A => ".png",
            >= 12 when IsFtypBrand(header, ["heic", "heix", "hevc", "hevx", "mif1", "msf1", "heis", "miaf"]) => ".heic",
            >= 12 when IsFtypBrand(header, ["avif", "avis"]) => ".avif",
            _ => fallback
        };

    }

    /// <summary>
    /// 根据文件头部特征字节嗅探视频的真实格式扩展名
    /// </summary>
    /// <param name="header">文件头部至少 16 字节的数据</param>
    /// <param name="fallback">无法识别时的回退扩展名</param>
    /// <returns>扩展名（含句点，如 .mp4、.mov）</returns>
    public static string DetectVideoExtension(ReadOnlySpan<byte> header, string fallback = ".mp4")
    {
        return header.Length switch
        {
            >= 12 when IsFtypBrand(header, ["qt  "]) => ".mov",
            >= 12 when (IsFtypBrand(header, ["mp41", "mp42", "isom", "iso2", "avc1", "MSNV", "hevc", "hvc1"]) || IsFtypBox(header)) => ".mp4",
            _ => fallback
        };

    }

    private static bool IsFtypBox(ReadOnlySpan<byte> header) => header.Length >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';

    private static bool IsFtypBrand(ReadOnlySpan<byte> header, string[] brands)
    {
        if (!IsFtypBox(header))
        {
            return false;
        }

        // Major brand (offset 8..12)
        var majorBrand = System.Text.Encoding.ASCII.GetString(header.Slice(8, Math.Min(4, header.Length - 8)));
        if (brands.Any(brand => string.Equals(majorBrand, brand, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Compatible brands (offset 16..end)
        for (var i = 16; i + 4 <= header.Length; i += 4)
        {
            var compBrand = System.Text.Encoding.ASCII.GetString(header.Slice(i, 4));
            if (brands.Any(brand => string.Equals(compBrand, brand, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
