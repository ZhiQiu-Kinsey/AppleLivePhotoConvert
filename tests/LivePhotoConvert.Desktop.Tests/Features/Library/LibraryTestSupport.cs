using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Pairing;
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

    /// <summary>写入拍摄时间相差 11 秒的实况对：扫描按配对校验判为待裁决。</summary>
    public void AddReviewPair(string stem)
    {
        Album.CreateInputFile(stem + ".jpg", Core.Tests.Support.SyntheticImages.Jpeg(64, 48, dateTimeOriginal: "2024:05:06 07:08:09", offsetTimeOriginal: "+08:00"));
        Album.CreateInputFile(stem + ".mov", Core.Tests.Support.SyntheticImages.Mov(new DateTime(2024, 5, 5, 23, 8, 20, DateTimeKind.Utc), 2));
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

/// <summary>不落盘的卡片：条目信息与扫描结果同构，文件并不存在（卡片不再访问磁盘）。</summary>
internal static class Cards
{
    public static readonly DateTime Modified = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>测试共享的本地化服务（不切换语言）。</summary>
    public static ILocalizer Localizer { get; } = new Localizer();

    public static LibraryFile File(string path, long length = 1000, DateTime? modified = null, DateTime? created = null) =>
        new(path, length, modified ?? Modified, created ?? modified ?? Modified);

    public static PhotoCardItemViewModel Of(LibraryItem item) => new(item, Localizer);

    public static LibraryItem ApplePairItem(string name, bool requiresReview = false, double aspect = 4.0 / 3.0, DateTime? taken = null)
    {
        var photo = File($"/album/{name}.heic", 3000);
        var video = File($"/album/{name}.mov", 5000);
        return new LibraryItem(LibraryItemKind.ApplePair, photo)
        {
            Video = video,
            PairCandidates = [new MediaPair(photo.Path, video.Path)],
            Header = Header(aspect),
            CaptureTimeLocal = taken ?? Modified.ToLocalTime(),
            PairValidation = requiresReview ? PairValidationResult.Reject(new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, 12.0, 3.0)) : PairValidationResult.Accept(),
            PairTimeDelta = requiresReview ? TimeSpan.FromSeconds(12) : TimeSpan.Zero
        };
    }

    public static PhotoCardItemViewModel ApplePair(string name, bool forced = false, bool selected = false) =>
        new(ApplePairItem(name), Localizer) { IsForceAccepted = forced, IsSelected = selected };

    public static PhotoCardItemViewModel MotionPhoto(string name, bool selected = false) =>
        new(new LibraryItem(LibraryItemKind.MotionPhoto, File($"/album/{name}.jpg", 9000))
        {
            Embedded = new EmbeddedVideo(6000, 3000, 6000),
            Header = Header(4.0 / 3.0),
            CaptureTimeLocal = Modified.ToLocalTime()
        }, Localizer) { IsSelected = selected };

    public static PhotoCardItemViewModel Still(string name, double aspect = 4.0 / 3.0, DateTime? taken = null, DateTime? created = null, DateTime? modified = null) =>
        new(new LibraryItem(LibraryItemKind.Still, File($"/album/{name}.jpg", 2000, modified, created))
        {
            Header = Header(aspect),
            CaptureTimeLocal = taken ?? Modified.ToLocalTime()
        }, Localizer);

    /// <summary>给定宽高比的头部（高 3000）。</summary>
    public static ImageHeader Header(double aspect) => new((int)Math.Round(3000 * aspect), 3000);
}
