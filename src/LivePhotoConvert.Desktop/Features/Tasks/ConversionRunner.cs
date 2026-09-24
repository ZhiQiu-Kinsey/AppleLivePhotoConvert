using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>执行一次转换；单个条目的失败记入报告，工具缺失等整批无法开始的问题以异常抛出。</summary>
public interface IConversionRunner
{
    Task<BatchReport> RunAsync(ConversionJob job, IProgress<BatchProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>按工具路径创建 Core 引擎；测试可替换为内存实现。</summary>
public interface IConversionEngines
{
    /// <exception cref="FileNotFoundException">找不到 ExifTool</exception>
    IMetadataService CreateMetadata(ToolPaths tools, int parallelism);

    IImageConverter CreateImageConverter(ToolPaths tools);

    /// <exception cref="FileNotFoundException">找不到 FFmpeg</exception>
    IVideoConverter CreateVideoConverter(ToolPaths tools);
}

public sealed class ExternalToolEngines : IConversionEngines
{
    public static ExternalToolEngines Instance { get; } = new();

    public IMetadataService CreateMetadata(ToolPaths tools, int parallelism) =>
        ExifToolMetadataService.Create(tools.ExifTool, parallelism);

    // 没有 heif-enc 时退回 Magick 编码，HEIC 仍可产出，只是速度与压缩率略差
    public IImageConverter CreateImageConverter(ToolPaths tools) =>
        ToolLocator.Find(HeifEncImageConverter.ExecutableName, tools.HeifEnc) is { } heifEnc
            ? HeifEncImageConverter.Create(heifEnc)
            : MagickImageConverter.Instance;

    public IVideoConverter CreateVideoConverter(ToolPaths tools) => FfmpegVideoConverter.Create(tools.Ffmpeg);
}

/// <summary>把任务映射到 Core 的合成、拆分与瘦身服务。</summary>
public sealed class ConversionRunner(IConversionEngines engines) : IConversionRunner
{
    public async Task<BatchReport> RunAsync(ConversionJob job, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var options = job.Options;
        await using var metadata = engines.CreateMetadata(job.Tools, job.Parallelism);
        var images = engines.CreateImageConverter(job.Tools);

        switch (job.Action)
        {
            case ConversionAction.ToAndroid:
                var merger = new MotionPhotoMerger(metadata, images, engines.CreateVideoConverter(job.Tools));
                return await merger.MergeAsync(
                    new MergeRequest
                    {
                        Candidates = job.Inputs.Pairs,
                        ForceAccepted = job.Inputs.ForceAccepted,
                        Output = RequireOutput(job),
                        Naming = options.Naming,
                        SourceAction = options.SourceAction,
                        Parallelism = job.Parallelism
                    },
                    progress,
                    cancellationToken);

            case ConversionAction.ToApple:
            case ConversionAction.Extract:
                var apple = job.Action == ConversionAction.ToApple;
                // 解包只做无损切片，不需要 FFmpeg
                var splitter = new MotionPhotoSplitter(metadata, images, apple ? engines.CreateVideoConverter(job.Tools) : null);
                return await splitter.SplitAsync(
                    new SplitRequest
                    {
                        Files = job.Inputs.Files,
                        Output = RequireOutput(job),
                        Target = apple ? SplitTarget.Apple : SplitTarget.Extract,
                        SourceAction = options.SourceAction,
                        HeicQuality = options.HeicQuality,
                        Parallelism = job.Parallelism
                    },
                    progress,
                    cancellationToken);

            case ConversionAction.Strip:
                return await new MotionPhotoStripper(metadata, images).StripAsync(
                    new StripRequest
                    {
                        Files = job.Inputs.Files,
                        Output = options.Output,
                        ConvertToHeic = options.ConvertToHeic,
                        HeicQuality = options.HeicQuality,
                        Parallelism = job.Parallelism
                    },
                    progress,
                    cancellationToken);

            default:
                throw new ArgumentOutOfRangeException(nameof(job), job.Action, null);
        }
    }

    private static OutputOptions RequireOutput(ConversionJob job) =>
        job.Options.Output ?? throw new InvalidOperationException($"{job.Action} 需要输出目录。");
}
