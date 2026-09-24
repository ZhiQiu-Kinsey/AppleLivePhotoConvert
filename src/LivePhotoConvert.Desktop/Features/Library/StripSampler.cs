using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>与瘦身任务相同的参数。</summary>
public sealed record StripSampleOptions(ToolPaths Tools, bool ConvertToHeic, int HeicQuality);

/// <summary>
/// 一张照片按瘦身任务真实处理后的产物，位于独占的临时目录；释放时删除该目录。
/// </summary>
/// <param name="SourcePath">原片</param>
/// <param name="ProductPath">瘦身产物；无需处理时就是原片</param>
/// <param name="OriginalBytes">原片连同确属同一实况的配对视频的体积</param>
/// <param name="ProductBytes">产物文件的实际大小</param>
/// <param name="Converted">产物是否经过 HEIC 编码</param>
/// <param name="EncoderName">产物所用的 HEIC 编码器</param>
public sealed record StripSample(
    string SourcePath,
    string ProductPath,
    long OriginalBytes,
    long ProductBytes,
    bool Converted,
    string EncoderName) : IDisposable
{
    internal TempWorkspace? Workspace { get; init; }

    /// <summary>产物所在的临时目录。</summary>
    public string? WorkspaceDirectory => Workspace?.Directory;

    public void Dispose() => Workspace?.Dispose();
}

/// <summary>对单张样张真实执行一次瘦身；测试可替换。</summary>
public interface IStripSampler
{
    /// <exception cref="FileNotFoundException">找不到 ExifTool 或 HEIC 编码器</exception>
    /// <exception cref="InvalidOperationException">样张处理失败</exception>
    Task<StripSample> SampleAsync(string photoPath, StripSampleOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// 调用 <see cref="MotionPhotoStripper"/> 把样张输出到临时目录：编码器选择、配对校验与增益图规则都与任务一致，
/// 对比与预估看到的就是任务的真实产物。
/// </summary>
public sealed class StripSampler(IConversionEngines engines, MetadataSessionPool sessions) : IStripSampler
{
    public Task<StripSample> SampleAsync(string photoPath, StripSampleOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(photoPath);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => SampleCoreAsync(Path.GetFullPath(photoPath), options, cancellationToken), cancellationToken);
    }

    private async Task<StripSample> SampleCoreAsync(string photoPath, StripSampleOptions options, CancellationToken cancellationToken)
    {
        var images = engines.CreateImageConverter(options.Tools);
        using var lease = sessions.Acquire(options.Tools);
        var stripper = new MotionPhotoStripper(lease.Service, images);
        var candidate = (await stripper.AnalyzeAsync([photoPath], cancellationToken))[0];
        if (candidate.AnalysisError is { } error)
        {
            throw new InvalidOperationException(error);
        }

        var converts = candidate.WillConvert(options.ConvertToHeic);
        if (converts && images is MagickImageConverter && !MagickImageConverter.SupportsHeicEncoding)
        {
            // 任务在这里会逐张失败；对比与预估直接指明缺少的编码器
            throw new ToolNotFoundException(HeifEncImageConverter.ExecutableName);
        }

        var workspace = new TempWorkspace();
        try
        {
            var report = await stripper.StripAsync(
                new StripRequest
                {
                    Files = [photoPath],
                    Output = new OutputOptions(workspace.Directory),
                    ConvertToHeic = options.ConvertToHeic,
                    HeicQuality = options.HeicQuality,
                    Parallelism = 1
                },
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = report.Items.Single();
            var product = outcome.Kind switch
            {
                OutcomeKind.Succeeded => outcome.Outputs.Single(),
                // 没有视频且无需转码：任务不会改动这张照片
                OutcomeKind.Skipped => photoPath,
                _ => throw new InvalidOperationException(outcome.Message)
            };

            return new StripSample(
                photoPath,
                product,
                candidate.OriginalBytes,
                new FileInfo(product).Length,
                converts && outcome.Kind == OutcomeKind.Succeeded,
                EncoderName(images))
            {
                Workspace = workspace
            };
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static string EncoderName(IImageConverter images) => images switch
    {
        HeifEncImageConverter => "heif-enc",
        MagickImageConverter => "Magick.NET",
        _ => "custom"
    };
}
