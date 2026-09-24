using System.Collections.Concurrent;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <param name="Count">含视频、可以瘦身的照片数</param>
/// <param name="OriginalBytes">这些照片（含配对视频）当前的体积</param>
/// <param name="EstimatedBytes">瘦身后的预计体积</param>
public sealed record StripEstimate(int Count, long OriginalBytes, long EstimatedBytes)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - EstimatedBytes);

    public double SavedPercent => OriginalBytes > 0 ? SavedBytes * 100.0 / OriginalBytes : 0;

    /// <summary>实测压缩比所用的样张数；为 0 时 HEIC 体积按经验比例估算（或无需转码）。</summary>
    public int SampledCount { get; init; }

    /// <summary>HEIC 相对转码前照片的体积比例（不超过 1）；无需转码时为 <c>null</c>。</summary>
    public double? HeicSizeRatio { get; init; }
}

/// <summary>瘦身空间预估与样张处理；测试可替换为不启动外部工具的实现。</summary>
public interface IStripEstimator
{
    /// <summary>对比预览复用同一个样张处理器，与预估共享 ExifTool 会话。</summary>
    IStripSampler Sampler { get; }

    Task<StripEstimate> EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, int heicQuality, CancellationToken cancellationToken);
}

/// <summary>
/// 视频字节按真实布局精确计算；转 HEIC 的图片体积用少量样张真实编码得到的压缩比（按像素数加权）外推。
/// 分析与样张处理用任务相同的引擎，判定（配对校验、增益图不转码、编码器选择）与实际执行一致。
/// </summary>
/// <remarks>
/// 样张结果按 路径+大小+修改时间+质量 缓存：同一批照片反复预估只编码一次，文件改动或质量变化后自动重新抽样。
/// 样张按路径的稳定散列挑选，选择范围小幅变化时大多仍落在已缓存的样张上。
/// </remarks>
public sealed class StripEstimator : IStripEstimator, IDisposable
{
    public const int MinSamples = 3;

    public const int MaxSamples = 5;

    /// <summary>适用照片达到这个数量才抽满 <see cref="MaxSamples"/> 张。</summary>
    public const int LargeSelection = 100;

    private const int SampleCacheCapacity = 512;

    private readonly IConversionEngines _engines;
    private readonly MetadataSessionPool _sessions;
    private readonly ConcurrentDictionary<SampleKey, SampleMeasure> _samples = new();

    public StripEstimator(IConversionEngines engines, TimeProvider? time = null)
        : this(engines, new MetadataSessionPool(engines, time ?? TimeProvider.System), sampler: null)
    {
    }

    internal StripEstimator(IConversionEngines engines, MetadataSessionPool sessions, IStripSampler? sampler)
    {
        _engines = engines;
        _sessions = sessions;
        Sampler = sampler ?? new StripSampler(engines, sessions);
    }

    public IStripSampler Sampler { get; }

    public async Task<StripEstimate> EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, int heicQuality, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return new StripEstimate(0, 0, 0);
        }

        IReadOnlyList<StripCandidate> candidates;
        using (var lease = _sessions.Acquire(tools))
        {
            candidates = await new MotionPhotoStripper(lease.Service, _engines.CreateImageConverter(tools)).AnalyzeAsync(files, cancellationToken);
        }

        var strippable = candidates.Where(c => c.AnalysisError is null && c.HasVideo).ToList();
        var converting = strippable.Where(c => c.WillConvert(convertToHeic)).ToList();
        var (ratio, sampled) = converting.Count == 0
            ? (StripCandidate.DefaultHeicSizeRatio, 0)
            : await MeasureHeicRatioAsync(converting, tools, heicQuality, cancellationToken);

        return new StripEstimate(
            strippable.Count,
            strippable.Sum(c => c.OriginalBytes),
            strippable.Sum(c => c.EstimateFinalBytes(convertToHeic, ratio)))
        {
            SampledCount = sampled,
            HeicSizeRatio = converting.Count == 0 ? null : ratio
        };
    }

    public void Dispose() => _sessions.Dispose();

    internal static int SampleCountFor(int candidates) =>
        Math.Min(candidates, candidates >= LargeSelection ? MaxSamples : MinSamples);

    /// <summary>按路径的稳定散列挑选样张：与枚举顺序无关，增减少量照片时大部分样张不变。</summary>
    internal static IReadOnlyList<StripCandidate> PickSamples(IReadOnlyList<StripCandidate> candidates) =>
        [.. candidates.OrderBy(c => StableHash(c.ImagePath)).ThenBy(c => c.ImagePath, StringComparer.Ordinal).Take(SampleCountFor(candidates.Count))];

    private async Task<(double Ratio, int Sampled)> MeasureHeicRatioAsync(
        IReadOnlyList<StripCandidate> candidates, ToolPaths tools, int heicQuality, CancellationToken cancellationToken)
    {
        double weighted = 0;
        double weights = 0;
        var sampled = 0;
        foreach (var candidate in PickSamples(candidates))
        {
            if (await MeasureAsync(candidate, tools, heicQuality, cancellationToken) is not { } measure)
            {
                continue;
            }

            // 大图对总体积贡献大，压缩比按像素数加权
            weighted += measure.Ratio * measure.Pixels;
            weights += measure.Pixels;
            sampled++;
        }

        return sampled == 0 ? (StripCandidate.DefaultHeicSizeRatio, 0) : (weighted / weights, sampled);
    }

    private async Task<SampleMeasure?> MeasureAsync(StripCandidate candidate, ToolPaths tools, int heicQuality, CancellationToken cancellationToken)
    {
        if (SampleKey.TryCreate(candidate.ImagePath, heicQuality) is not { } key)
        {
            return null;
        }

        if (_samples.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            using var sample = await Sampler.SampleAsync(candidate.ImagePath, new StripSampleOptions(tools, ConvertToHeic: true, heicQuality), cancellationToken);
            if (!sample.Converted && !sample.KeptOriginalFormat || candidate.PhotoBytes <= 0)
            {
                return null;
            }

            var pixels = FastImageHeaderReader.TryReadDimensions(candidate.ImagePath, out var dimensions)
                ? Math.Max(1L, (long)dimensions.Width * dimensions.Height)
                : 1L;
            // HEIC 更大时任务保留原格式，体积不会超过只剥离视频的结果，比例按 1 封顶
            var ratio = sample.KeptOriginalFormat ? 1 : Math.Min(1, (double)sample.ProductBytes / candidate.PhotoBytes);
            var measure = new SampleMeasure(ratio, pixels);
            if (_samples.Count >= SampleCacheCapacity)
            {
                _samples.Clear();
            }

            _samples[key] = measure;
            return measure;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ToolNotFoundException))
        {
            // 单张样张失败不影响外推；缺少编码器时整体无法估算，交给调用方提示
            ErrorLogger.Log(ex, "瘦身预估样张");
            return null;
        }
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    private readonly record struct SampleMeasure(double Ratio, long Pixels);

    private readonly record struct SampleKey(string Path, long Length, long LastWriteTicks, int Quality)
    {
        public static SampleKey? TryCreate(string path, int quality)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new SampleKey(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, quality) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}
