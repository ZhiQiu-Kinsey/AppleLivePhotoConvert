using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>
/// 缩略图缓存键。源文件被改写（大小或修改时间变化）或档位不同都会得到新键，旧文件由容量淘汰清理。
/// </summary>
public readonly record struct ThumbnailKey
{
    /// <summary>生成管线的版本；输出格式、画质或色彩处理变化时递增，使旧缓存整体失效。</summary>
    public const int FormatVersion = 1;

    private ThumbnailKey(string hash, int tier)
    {
        Hash = hash;
        Tier = tier;
    }

    /// <summary>64 位小写十六进制 SHA-256。</summary>
    public string Hash { get; }

    public int Tier { get; }

    /// <summary>缓存根目录下的相对路径：按前 2 位分子目录，避免单目录文件过多拖慢枚举。</summary>
    public string RelativePath => Path.Combine(Hash[..2], Hash + ".jpg");

    public static ThumbnailKey Create(string path, long length, DateTime lastWriteTimeUtc, int tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var utc = lastWriteTimeUtc.Kind == DateTimeKind.Local ? lastWriteTimeUtc.ToUniversalTime() : lastWriteTimeUtc;
        var text = string.Join('|',
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            NormalizePath(path),
            length.ToString(CultureInfo.InvariantCulture),
            utc.Ticks.ToString(CultureInfo.InvariantCulture),
            tier.ToString(CultureInfo.InvariantCulture));
        return new ThumbnailKey(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), tier);
    }

    /// <summary>
    /// 同一文件的不同写法（相对路径、末尾分隔符、Windows/macOS 默认不区分大小写的文件系统上的大小写）映射到同一键。
    /// </summary>
    internal static string NormalizePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }

    public override string ToString() => Hash;
}
