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
}

/// <summary>瘦身空间预估；测试可替换为不启动外部工具的实现。</summary>
public interface IStripEstimator
{
    Task<StripEstimate> EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, CancellationToken cancellationToken);
}

/// <summary>用任务相同的引擎做只读分析，预估与实际执行的判定（配对校验、增益图不转码）一致。</summary>
public sealed class StripEstimator(IConversionEngines engines) : IStripEstimator
{
    public async Task<StripEstimate> EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return new StripEstimate(0, 0, 0);
        }

        await using var metadata = engines.CreateMetadata(tools, 1);
        var candidates = await new MotionPhotoStripper(metadata, engines.CreateImageConverter(tools)).AnalyzeAsync(files, cancellationToken);
        var strippable = candidates.Where(c => c.AnalysisError is null && c.HasVideo).ToList();
        return new StripEstimate(
            strippable.Count,
            strippable.Sum(c => c.OriginalBytes),
            strippable.Sum(c => c.EstimateFinalBytes(convertToHeic)));
    }
}
