using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>按预设回答的工具探测。</summary>
internal sealed class FakeToolAvailability : IToolAvailability
{
    public HashSet<RequiredTool> Missing { get; } = [];

    public int Probes { get; private set; }

    public bool IsAvailable(RequiredTool tool, ToolPaths paths)
    {
        lock (Missing)
        {
            Probes++;
            return !Missing.Contains(tool);
        }
    }
}

internal sealed class FakeDiskSpace : IDiskSpaceGuard
{
    public bool HasEnoughSpace { get; set; } = true;

    public List<(string Directory, long Bytes)> Checks { get; } = [];

    public (bool HasEnoughSpace, long RequiredBytes, long AvailableBytes) Check(string targetDirectory, long totalSourceBytes)
    {
        lock (Checks)
        {
            Checks.Add((targetDirectory, totalSourceBytes));
        }

        return (HasEnoughSpace, 2_000_000, HasEnoughSpace ? 4_000_000 : 1_000);
    }
}

/// <summary>记录每次估算的输入与取消令牌；可设置为阻塞直到测试放行。</summary>
internal sealed class FakeStripEstimator : IStripEstimator
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<(IReadOnlyList<string> Files, bool ConvertToHeic, CancellationToken Token)> Requests { get; } = [];

    public TaskCompletionSource<StripEstimate>? Gate { get; set; }

    public StripEstimate Result { get; set; } = new(2, 10_000_000, 1_000_000);

    public Task<StripEstimate> EstimateAsync(IReadOnlyList<string> files, ToolPaths tools, bool convertToHeic, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests)
        {
            Requests.Add((files, convertToHeic, cancellationToken));
        }

        return Gate is { } gate ? gate.Task.WaitAsync(cancellationToken) : Task.FromResult(Result);
    }
}

/// <summary>桌面宿主 + 检查器的可控替身：工具、磁盘、预估、时钟与转换执行都不接触真实环境。</summary>
internal sealed class InspectorFixture : IDisposable
{
    /// <param name="settings">在任何页面创建之前修改设置</param>
    /// <param name="run">任务执行脚本；默认立即返回空报告</param>
    /// <param name="settingsPath">使用已有的设置文件（模拟重新启动）</param>
    public InspectorFixture(Action<DesktopSettings>? settings = null, Func<ConversionJob, Task<BatchReport>>? run = null, string? settingsPath = null)
    {
        Runner = new ScriptedRunner((job, _, _) => run?.Invoke(job) ?? Task.FromResult(Jobs.Report()));
        Host = new DesktopTestHost(services =>
        {
            if (settingsPath is not null)
            {
                services.AddSingleton(_ => new SettingsStore(settingsPath));
            }

            services.AddSingleton<IToolAvailability>(Tools);
            services.AddSingleton<IDiskSpaceGuard>(Disk);
            services.AddSingleton<IStripEstimator>(Estimator);
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IConversionRunner>(Runner);
        });
        if (settings is not null)
        {
            Host.Settings.Update(settings);
        }

        Album = new TestSandbox();
    }

    public DesktopTestHost Host { get; }

    public TestSandbox Album { get; }

    public FakeToolAvailability Tools { get; } = new();

    public FakeDiskSpace Disk { get; } = new();

    public FakeStripEstimator Estimator { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

    public ScriptedRunner Runner { get; }

    public LibraryViewModel Library => Host.Get<LibraryViewModel>();

    public InspectorViewModel Inspector => Host.Get<InspectorViewModel>();

    public IDialogService Dialogs => Host.Get<IDialogService>();

    /// <summary>写入苹果实况对（同名照片 + MOV）。</summary>
    public void AddApplePair(string stem)
    {
        Album.CreateInputFile(stem + ".heic", new byte[3000]);
        Album.CreateInputFile(stem + ".mov", new byte[5000]);
    }

    /// <summary>写入结构合法的安卓动态照片。</summary>
    public string AddMotionPhoto(string stem) => Album.CreateInputFile(stem + ".jpg", SyntheticMedia.MotionPhoto());

    /// <summary>把相册目录交给图库并按当前扫描模式扫描完成。</summary>
    public async Task ScanAsync()
    {
        Library.AlbumDirectory = Album.InputDirectory;
        await Library.RefreshAlbumAsync();
    }

    public void Dispose()
    {
        Album.Dispose();
        Host.Dispose();
    }
}

internal static class DialogWaits
{
    /// <summary>检查器在后台探测工具后才弹窗，轮询等待指定类型的弹窗出现。</summary>
    public static async Task<T> WaitForDialogAsync<T>(this IDialogService dialogs, int seconds = 10) where T : DialogViewModel
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (dialogs.Current is T dialog)
            {
                return dialog;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"等待 {typeof(T).Name} 超时，当前弹窗：{dialogs.Current?.GetType().Name ?? "无"}");
    }
}

internal static class Cards
{
    public static PhotoCardItemViewModel ApplePair(string name, bool forced = false, bool selected = true) => new()
    {
        Key = name,
        PhotoPath = $"/album/{name}.heic",
        VideoPath = $"/album/{name}.mov",
        IsForceAccepted = forced,
        IsSelected = selected
    };

    public static PhotoCardItemViewModel MotionPhoto(string name, bool selected = true) => new()
    {
        Key = name,
        PhotoPath = $"/album/{name}.jpg",
        IsMotionPhoto = true,
        IsSelected = selected
    };

    /// <summary>非苹果对、非动态照片的静态图（例如播放后才补上临时视频的情况以外）。</summary>
    public static PhotoCardItemViewModel Still(string name) => new()
    {
        Key = name,
        PhotoPath = $"/album/{name}.jpg"
    };
}
