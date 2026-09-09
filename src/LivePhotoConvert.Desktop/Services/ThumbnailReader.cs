using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 缩略图读取与磁盘缓存管理器。
/// </summary>
public sealed class ThumbnailReader
{
    public sealed record Result(byte[] ImageBytes, int Width, int Height, DateTime? TakenLocal)
    {
        public byte[] PngBytes => ImageBytes;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LivePhotoConvert", "cache", "thumbs");

    static ThumbnailReader()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 从本地磁盘缓存读取缩略图。
    /// </summary>
    public Result? TryGetFromCacheOnly(string photoPath, int maxSize)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath))
            {
                return null;
            }

            var fi = new FileInfo(photoPath);
            string cacheKey = ComputeHash($"v2_{photoPath}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}_{maxSize}");
            string cacheFile = Path.Combine(CacheDir, $"{cacheKey}.jpg");
            string metaFile = Path.Combine(CacheDir, $"{cacheKey}.meta");

            if (File.Exists(cacheFile) && File.Exists(metaFile))
            {
                string[] metaLines = File.ReadAllLines(metaFile);
                if (metaLines.Length >= 2 &&
                    int.TryParse(metaLines[0], out int cachedWidth) &&
                    int.TryParse(metaLines[1], out int cachedHeight))
                {
                    DateTime? cachedTaken = null;
                    if (metaLines.Length >= 3 && long.TryParse(metaLines[2], out long ticks) && ticks > 0)
                    {
                        cachedTaken = new DateTime(ticks, DateTimeKind.Local);
                    }

                    byte[] cachedBytes = File.ReadAllBytes(cacheFile);
                    return new Result(cachedBytes, cachedWidth, cachedHeight, cachedTaken);
                }
            }
        }
        catch
        {
            // 缓存损坏时安全返回 null，走后续解码
        }

        return null;
    }

    /// <summary>
    /// 读取或生成缩略图（优先命中磁盘缓存，未命中时优先提取内嵌 EXIF 缩略图并写盘）
    /// </summary>
    public Result? Read(string photoPath, int maxSize)
    {
        try
        {
            // 1. 优先尝试磁盘持久化缓存
            var cached = TryGetFromCacheOnly(photoPath, maxSize);
            if (cached is not null)
            {
                return cached;
            }

            if (string.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath))
            {
                return null;
            }

            var fi = new FileInfo(photoPath);
            string cacheKey = ComputeHash($"v2_{photoPath}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}_{maxSize}");
            string cacheFile = Path.Combine(CacheDir, $"{cacheKey}.jpg");
            string metaFile = Path.Combine(CacheDir, $"{cacheKey}.meta");

            byte[]? jpgBytes;
            int origWidth;
            int origHeight;
            DateTime? taken = null;

            // 2. 优先使用 Windows Shell 提取缩略图，避免解码完整原始图像点阵
            var shellResult = WindowsShellThumbnail.TryExtractThumbnail(photoPath, maxSize);
            if (shellResult.HasValue)
            {
                jpgBytes = shellResult.Value.JpgBytes;

                // 快速 Ping 原图头部获取真实相机像素分辨率与拍摄时间（仅读头部，毫秒级）
                try
                {
                    using var ping = new MagickImage();
                    ping.Ping(photoPath);
                    origWidth = (int)ping.Width;
                    origHeight = (int)ping.Height;

                    IExifProfile? exif = ping.GetExifProfile();
                    if (exif is not null)
                    {
                        string? raw = exif.GetValue(ExifTag.DateTimeOriginal)?.Value;
                        if (!string.IsNullOrWhiteSpace(raw) &&
                            DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                        {
                            taken = parsed;
                        }
                    }
                }
                catch
                {
                    origWidth = shellResult.Value.Width;
                    origHeight = shellResult.Value.Height;
                }

                taken ??= fi.LastWriteTime;
            }
            else
            {
                // 3. Windows Shell 未命中时，Ping 读取原始元数据并尝试 EXIF 缩略图
                using var pingImage = new MagickImage();
                pingImage.Ping(photoPath);

                origWidth = (int)pingImage.Width;
                origHeight = (int)pingImage.Height;

                IExifProfile? exif = pingImage.GetExifProfile();
                if (exif is not null)
                {
                    string? raw = exif.GetValue(ExifTag.DateTimeOriginal)?.Value;
                    if (!string.IsNullOrWhiteSpace(raw) &&
                        DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                    {
                        taken = parsed;
                    }
                }

                IMagickImage<byte>? thumbImage = null;
                try
                {
                    thumbImage = exif?.CreateThumbnail();
                }
                catch
                {
                    // ignored
                }

                if (thumbImage is not null)
                {
                    using (thumbImage)
                    {
                        thumbImage.AutoOrient();
                        thumbImage.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize));
                        thumbImage.Format = MagickFormat.Jpeg;
                        thumbImage.Quality = 92;
                        jpgBytes = thumbImage.ToByteArray();
                    }
                }
                else
                {
                    // 无内嵌缩略图时解码并降采样
                    using var fullImage = new MagickImage(photoPath);
                    fullImage.AutoOrient();
                    fullImage.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize));
                    fullImage.Format = MagickFormat.Jpeg;
                    fullImage.Quality = 92;
                    jpgBytes = fullImage.ToByteArray();
                }
            }

            if (jpgBytes is null || jpgBytes.Length == 0) return null;

            // 4. 写入本地磁盘缓存
            try
            {
                File.WriteAllBytes(cacheFile, jpgBytes);
                string metaContent = $"{origWidth}\n{origHeight}\n{(taken.HasValue ? taken.Value.Ticks : 0)}";
                File.WriteAllText(metaFile, metaContent);
            }
            catch
            {
                // ignored
            }

            return new Result(jpgBytes, origWidth, origHeight, taken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }
}
