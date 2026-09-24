using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Services;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

/// <summary>按脚本执行的转换器：测试直接控制进度、阻塞与结果。</summary>
internal sealed class ScriptedRunner(Func<ConversionJob, IProgress<BatchProgress>?, CancellationToken, Task<BatchReport>> script) : IConversionRunner
{
    public List<ConversionJob> Jobs { get; } = [];

    public Task<BatchReport> RunAsync(ConversionJob job, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
        }

        return script(job, progress, cancellationToken);
    }

    public static ScriptedRunner Returning(BatchReport report) => new((_, _, _) => Task.FromResult(report));
}

/// <summary>返回 Core 单元测试的内存引擎，并记录创建了哪些引擎。</summary>
internal sealed class FakeEngines : IConversionEngines
{
    public FakeMetadataService Metadata { get; } = new();

    public FakeImageConverter Images { get; } = new() { HeicSize = 900 };

    public FakeVideoConverter Videos { get; } = new();

    public int VideoConvertersCreated { get; private set; }

    public int? MetadataParallelism { get; private set; }

    public IMetadataService CreateMetadata(ToolPaths tools, int parallelism)
    {
        MetadataParallelism = parallelism;
        return Metadata;
    }

    public IImageConverter CreateImageConverter(ToolPaths tools) => Images;

    public IVideoConverter CreateVideoConverter(ToolPaths tools)
    {
        VideoConvertersCreated++;
        return Videos;
    }
}

/// <summary>桌面测试宿主 + 同步投递进度的任务中心 + 可控时钟。</summary>
internal sealed class TaskCenterFixture : IDisposable
{
    public TaskCenterFixture(IConversionRunner runner)
    {
        Host = new DesktopTestHost();
        // 开启完成后打开目录，才能通过外壳替身断言完成效果是否执行
        Host.Settings.Update(s => s.AutoOpenOutput = true);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        Center = new TaskCenter(
            runner,
            Host.Localizer,
            Host.Get<INavigator>(),
            Host.Get<CompletionEffects>(),
            Host.Shell,
            Host.Get<IDialogService>(),
            Host.FilePicker,
            Time,
            action => action());
    }

    public DesktopTestHost Host { get; }

    public FakeTimeProvider Time { get; }

    public TaskCenter Center { get; }

    public void Dispose() => Host.Dispose();
}

internal static class Jobs
{
    public static ConversionJob Files(ConversionAction action, string? output, params string[] files) =>
        new(action, new ConversionOptions { Output = output is null ? null : new OutputOptions(output), InPlaceLocation = output is null ? "in-place" : null }, new ConversionInputs { Files = files });

    public static BatchReport Report(params ItemOutcome[] items) => new(items, TimeSpan.FromSeconds(4), Canceled: false);

    public static async Task<T> Within<T>(this Task<T> task, int seconds = 10) =>
        await task.WaitAsync(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);

    public static async Task Within(this Task task, int seconds = 10) =>
        await task.WaitAsync(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);
}
