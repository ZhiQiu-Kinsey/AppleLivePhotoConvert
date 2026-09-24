namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>按源格式划分的解码类别，各自限制并发。</summary>
internal enum ThumbnailDecodeKind
{
    /// <summary>可用 DCT 缩放解码，峰值与输出尺寸相关。</summary>
    Jpeg,

    /// <summary>HEIC / AVIF：libheif 只能整图解码，1200 万像素一次生成的峰值增量实测约 60MB，JPEG 经 DCT 缩放约 10MB。</summary>
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
