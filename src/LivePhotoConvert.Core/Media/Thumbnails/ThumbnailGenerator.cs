using ImageMagick;
using ImageMagick.Formats;

namespace LivePhotoConvert.Core.Media.Thumbnails;

/// <summary>
/// 读取完整像素。<paramref name="settings"/> 已带缩放解码提示；测试可注入以计数并发或检查参数。
/// </summary>
public delegate IMagickImage<byte> ThumbnailDecoder(string path, MagickReadSettings settings);

/// <summary>
/// 缩略图生成：磁盘缓存 → 平台缩略图 → EXIF 内嵌缩略图 → 完整解码，产出方向正确、sRGB、按档位缩放的 JPEG 并写入缓存。
/// 方法含同步文件 I/O，不要在 UI 线程调用。
/// </summary>
public sealed class ThumbnailGenerator
{
    private const int DecodedQuality = 92;

    /// <summary>平台缩略图本身已是有损压缩过的，再编码用更高质量减少二次损失。</summary>
    private const int PlatformQuality = 95;

    /// <summary>平台缩略图略小于档位（系统缓存尺寸与档位不对齐）仍可接受，再小就会明显发虚。</summary>
    private const double PlatformMinRatio = 0.9;

    private readonly IThumbnailSource? _platform;
    private readonly ThumbnailDecodeLimits _limits;
    private readonly ThumbnailDecoder _decoder;

    public ThumbnailGenerator(
        ThumbnailDiskCache cache,
        IThumbnailSource? platformSource = null,
        ThumbnailDecodeLimits? limits = null,
        ThumbnailDecoder? decoder = null)
    {
        Cache = cache;
        _platform = platformSource;
        _limits = limits ?? ThumbnailDecodeLimits.Shared;
        _decoder = decoder ?? DefaultDecoder;
    }

    public ThumbnailDiskCache Cache { get; }

    /// <summary>只查磁盘缓存，不生成。</summary>
    public ThumbnailResult? TryGetCached(ThumbnailRequest request) =>
        Cache.TryGet(request.Key, out var path) ? new ThumbnailResult(ThumbnailOrigin.Cache, path, null) : null;

    /// <summary>
    /// 取缩略图，未命中缓存时生成并写入缓存。源文件无法解码时返回 <c>null</c>；取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<ThumbnailResult?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCached(request) is { } cached)
        {
            return cached;
        }

        if (await ProduceAsync(request, cancellationToken).ConfigureAwait(false) is not { } produced)
        {
            return null;
        }

        var (data, origin) = produced;
        var stored = Cache.TryPut(request.Key, data);
        return new ThumbnailResult(origin, stored ? Cache.GetPath(request.Key) : null, data);
    }

    private async Task<(byte[] Data, ThumbnailOrigin Origin)?> ProduceAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        if (_platform is not null &&
            await TryPlatformAsync(_platform, request, cancellationToken).ConfigureAwait(false) is { } fromPlatform)
        {
            return (fromPlatform, ThumbnailOrigin.Platform);
        }

        var probe = await Task.Run(() => Probe(request), cancellationToken).ConfigureAwait(false);
        if (probe.Embedded is { } embedded)
        {
            return (embedded, ThumbnailOrigin.Embedded);
        }

        var gate = _limits.For(probe.Kind);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var decoded = await Task.Run(() => Decode(request, probe), cancellationToken).ConfigureAwait(false);
            return decoded is null ? null : (decoded, ThumbnailOrigin.Decoded);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<byte[]?> TryPlatformAsync(IThumbnailSource source, ThumbnailRequest request, CancellationToken cancellationToken)
    {
        // 不知道比例时取景框放宽到 2:1：竖图与常见横图都能拿到档位高度
        var (boxWidth, boxHeight) = request.Header is { Width: > 0, Height: > 0 } header
            ? ThumbnailTiers.Fit(header.Width, header.Height, request.Tier)
            : (request.Tier * 2, request.Tier);

        IMagickImage<byte>? image;
        try
        {
            image = await source.TryGetAsync(request.Path, boxWidth, boxHeight, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (image is null)
        {
            return null;
        }

        using (image)
        {
            // 系统只给出更小的缓存（如 256px）时放弃，交给解码路径，避免放大发虚
            if (image.Height < Math.Ceiling(boxHeight * PlatformMinRatio))
            {
                return null;
            }

            return await Task.Run(() => TryEncode(image, request.Tier, null, PlatformQuality), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProbeResult Probe(ThumbnailRequest request)
    {
        var kind = DetectKind(request.Path);
        int storedWidth = 0, storedHeight = 0;
        var orientation = OrientationType.Undefined;
        byte[]? embedded = null;
        try
        {
            using var ping = new MagickImage();
            ping.Ping(request.Path, FirstFrame());
            storedWidth = (int)ping.Width;
            storedHeight = (int)ping.Height;
            orientation = ping.Orientation;
            embedded = TryEmbedded(ping, request.Tier);
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            // 读不到头部仍尝试完整解码，由解码结果决定成败
        }

        if ((storedWidth <= 0 || storedHeight <= 0) && request.Header is { Width: > 0, Height: > 0 } header)
        {
            orientation = (OrientationType)header.Orientation;
            (storedWidth, storedHeight) = IsTransposed(orientation) ? (header.Height, header.Width) : (header.Width, header.Height);
        }

        return new ProbeResult(kind, storedWidth, storedHeight, orientation, embedded);
    }

    /// <summary>
    /// EXIF 内嵌缩略图通常只有 160px；转正后不低于档位才用，否则放大后明显模糊。
    /// </summary>
    private static byte[]? TryEmbedded(MagickImage ping, int tier)
    {
        try
        {
            using var thumbnail = ping.GetExifProfile()?.CreateThumbnail();
            if (thumbnail is null)
            {
                return null;
            }

            var orientedHeight = IsTransposed(ping.Orientation) ? thumbnail.Width : thumbnail.Height;
            if (orientedHeight < tier)
            {
                return null;
            }

            // 内嵌图自身不带方向与 profile，沿用主图的
            thumbnail.Orientation = ping.Orientation;
            return TryEncode(thumbnail, tier, ping.GetColorProfile(), DecodedQuality);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private byte[]? Decode(ThumbnailRequest request, ProbeResult probe)
    {
        try
        {
            var plan = probe.Kind == ThumbnailDecodeKind.Jpeg ? PlanJpegScale(probe, request.Tier) : null;
            var settings = FirstFrame();
            if (plan is { } p)
            {
                settings.SetDefines(new JpegReadDefines { Size = p.Hint });
            }

            var image = _decoder(request.Path, settings);
            try
            {
                if (plan is { } q && (image.Width < q.TargetWidth || image.Height < q.TargetHeight))
                {
                    // 解码器选了比目标更小的比例时宁可完整解码，也不输出低于档位的图
                    image.Dispose();
                    image = _decoder(request.Path, FirstFrame());
                }

                return Encode(image, request.Tier, null, DecodedQuality);
            }
            finally
            {
                image.Dispose();
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 规划 JPEG DCT 缩放：jpeg:size 作用于解码前（存储方向）的像素网格，目标尺寸必须先换回存储方向。
    /// libjpeg 只支持 M/8 比例，取能覆盖目标的最小 M，并请求恰好 M/8 的尺寸，使 ImageMagick 的取整不会落到更小的比例。
    /// </summary>
    internal static (MagickGeometry Hint, int TargetWidth, int TargetHeight)? PlanJpegScale(ProbeResult probe, int tier)
    {
        if (probe.StoredWidth <= 0 || probe.StoredHeight <= 0)
        {
            return null;
        }

        var transposed = IsTransposed(probe.Orientation);
        var (orientedWidth, orientedHeight) = transposed
            ? (probe.StoredHeight, probe.StoredWidth)
            : (probe.StoredWidth, probe.StoredHeight);
        var (fitWidth, fitHeight) = ThumbnailTiers.Fit(orientedWidth, orientedHeight, tier);
        var (targetWidth, targetHeight) = transposed ? (fitHeight, fitWidth) : (fitWidth, fitHeight);

        var eighths = (int)Math.Ceiling(8.0 * Math.Max(
            (double)targetWidth / probe.StoredWidth,
            (double)targetHeight / probe.StoredHeight));
        if (eighths >= 8)
        {
            return null;
        }

        eighths = Math.Max(1, eighths);
        var hint = new MagickGeometry(
            (uint)Math.Ceiling(probe.StoredWidth * eighths / 8.0),
            (uint)Math.Ceiling(probe.StoredHeight * eighths / 8.0));
        return (hint, targetWidth, targetHeight);
    }

    private static byte[]? TryEncode(IMagickImage<byte> image, int tier, IColorProfile? sourceProfile, int quality)
    {
        try
        {
            return Encode(image, tier, sourceProfile, quality);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// 规范化并编码：转正 → 只缩小到档位 → 转到 sRGB → 去掉全部 profile → JPEG。
    /// 缩略图不带 profile，显示端一律按 sRGB 解释，所以必须在这里完成色彩转换。
    /// </summary>
    internal static byte[] Encode(IMagickImage<byte> image, int tier, IColorProfile? sourceProfile, int quality)
    {
        // 缩放与 Strip 之前先拿到 ICC；内嵌缩略图没有自己的 profile，用主图的
        var profile = image.GetColorProfile() ?? sourceProfile;

        image.AutoOrient();
        var (width, height) = ThumbnailTiers.Fit((int)image.Width, (int)image.Height, tier);
        if (width != image.Width || height != image.Height)
        {
            image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        }

        ToSrgb(image, profile);

        if (image.HasAlpha)
        {
            // JPEG 无透明通道，直接丢弃 alpha 会露出透明像素里残留的颜色
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Alpha(AlphaOption.Off);
        }

        image.Strip();
        image.Format = MagickFormat.Jpeg;
        image.Quality = (uint)quality;
        // 画廊画质优先：4:4:4 保留彩色边缘与文字清晰度，缓存体积约增加三成
        image.Settings.SetDefines(new JpegWriteDefines { SamplingFactor = JpegSamplingFactor.Ratio444 });
        return image.ToByteArray();
    }

    private static void ToSrgb(IMagickImage<byte> image, IColorProfile? profile)
    {
        if (profile is not null)
        {
            try
            {
                image.TransformColorSpace(profile, ColorProfiles.SRGB);
                return;
            }
            catch (MagickException)
            {
                // 损坏的 ICC：退回按色彩空间处理
            }
        }

        if (image.ColorSpace == ColorSpace.CMYK)
        {
            image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
        }
        else if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.Gray))
        {
            image.ColorSpace = ColorSpace.sRGB;
        }
    }

    private static ThumbnailDecodeKind DetectKind(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> header = stackalloc byte[32];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return MediaFileTypes.DetectPhotoExtension(header[..read], fallback: "") switch
            {
                ".jpg" => ThumbnailDecodeKind.Jpeg,
                ".heic" or ".avif" => ThumbnailDecodeKind.Heif,
                _ => ThumbnailDecodeKind.Other,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ThumbnailDecodeKind.Other;
        }
    }

    private static bool IsTransposed(OrientationType orientation) =>
        orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom;

    /// <summary>多帧格式（GIF、TIFF）只读第一帧。</summary>
    private static MagickReadSettings FirstFrame() => new() { FrameIndex = 0, FrameCount = 1 };

    private static IMagickImage<byte> DefaultDecoder(string path, MagickReadSettings settings) => new MagickImage(path, settings);

    internal readonly record struct ProbeResult(
        ThumbnailDecodeKind Kind,
        int StoredWidth,
        int StoredHeight,
        OrientationType Orientation,
        byte[]? Embedded);
}
