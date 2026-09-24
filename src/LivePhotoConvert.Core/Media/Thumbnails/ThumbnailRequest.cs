namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>
/// 一次缩略图请求。文件大小与修改时间由调用方提供（扫描时已取得），避免重复 stat。
/// </summary>
public sealed record ThumbnailRequest
{
    public ThumbnailRequest(string path, long length, DateTime lastWriteTimeUtc, int tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!ThumbnailTiers.IsTier(tier))
        {
            // 任意高度都会产生独立缓存文件，只接受档位使缓存可复用
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "必须是 ThumbnailTiers 中的档位。");
        }

        Path = path;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Tier = tier;
        Key = ThumbnailKey.Create(path, length, lastWriteTimeUtc, tier);
    }

    public string Path { get; }

    public long Length { get; }

    public DateTime LastWriteTimeUtc { get; }

    /// <summary>目标高度档位，见 <see cref="ThumbnailTiers"/>。</summary>
    public int Tier { get; }

    public ThumbnailKey Key { get; }

    /// <summary>
    /// 可选的源图信息：转正后的宽高与 EXIF 方向（1～8）。提供时用于平台缩略图的取景框与尺寸校验，
    /// 读不到文件头时也用它规划 JPEG 缩放解码。
    /// </summary>
    public (int Width, int Height, int Orientation)? Header { get; init; }

    /// <summary>从文件系统读取大小与修改时间构造请求。</summary>
    public static ThumbnailRequest ForFile(string path, int tier)
    {
        var info = new FileInfo(path);
        return new ThumbnailRequest(info.FullName, info.Length, info.LastWriteTimeUtc, tier);
    }
}

/// <summary>缩略图来自哪一级来源。</summary>
public enum ThumbnailOrigin
{
    Cache,
    Platform,
    Embedded,
    Decoded,
}

/// <summary>
/// 生成结果（sRGB、已转正、无 profile 的 JPEG）。新生成的结果带内存数据；缓存命中只带路径，
/// 文件可能在打开前被容量清理删除，调用方应把打开失败当作未命中。
/// </summary>
public sealed record ThumbnailResult(ThumbnailOrigin Origin, string? CachePath, byte[]? Data)
{
    public Stream OpenRead() => Data is not null
        ? new MemoryStream(Data, writable: false)
        : new FileStream(
            CachePath ?? throw new InvalidOperationException("结果既无数据也无缓存路径。"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
}
