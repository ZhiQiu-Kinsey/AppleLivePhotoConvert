namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>按源格式划分的解码类别，各自限制并发。</summary>
internal enum ThumbnailDecodeKind
{
    /// <summary>可用 DCT 缩放解码，峰值与输出尺寸相关。</summary>
    Jpeg,

    /// <summary>HEIC / AVIF：libheif 只能整图解码，峰值约每像素 15 字节（1200 万像素约 180MB）。</summary>
    Heif,

    Other,
}

/// <summary>
/// 完整解码的并发上限，用来压住多张原图同时驻留内存的峰值。
/// 默认实例进程内共享，使多个生成器（画廊、预览）合计也不超过上限。
/// </summary>
public sealed class ThumbnailDecodeLimits(int jpeg = 2, int heif = 1, int other = 2)
{
    private readonly SemaphoreSlim _jpeg = new(Positive(jpeg), Positive(jpeg));
    private readonly SemaphoreSlim _heif = new(Positive(heif), Positive(heif));
    private readonly SemaphoreSlim _other = new(Positive(other), Positive(other));

    public static ThumbnailDecodeLimits Shared { get; } = new();

    public int MaxJpeg { get; } = jpeg;

    public int MaxHeif { get; } = heif;

    public int MaxOther { get; } = other;

    /// <summary>三类合计的最大同时解码数；宿主据此设置 Magick 线程数。</summary>
    public int MaxTotal => MaxJpeg + MaxHeif + MaxOther;

    internal SemaphoreSlim For(ThumbnailDecodeKind kind) => kind switch
    {
        ThumbnailDecodeKind.Jpeg => _jpeg,
        ThumbnailDecodeKind.Heif => _heif,
        _ => _other,
    };

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}
