using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Playback;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;
using LivePhotoConvert.Desktop.Tests.Features.Tools;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Docs;

/// <summary>
/// 生成 README 截图（docs/screenshots/）。只在设置了环境变量 <see cref="OutputVariable"/> 时工作：
/// <code>LPC_DOCS_SCREENSHOTS=docs/screenshots dotnet test tests/LivePhotoConvert.Desktop.Tests --filter "FullyQualifiedName~ReadmeScreenshotTests"</code>
/// 未设置时直接通过而不是跳过：CI 的 Linux 任务限制跳过数，文档截图不应占用这个额度。
/// 需要 FFmpeg（编码示例视频与 QuickLook 播放）；有 heif-enc 时瘦身预估与对比使用真实编码结果。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ReadmeScreenshotTests
{
    public const string OutputVariable = "LPC_DOCS_SCREENSHOTS";

    private static readonly string[] Languages = ["zh", "en"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task GenerateReadmeScreenshots()
    {
        var output = ResolveOutputDirectory();
        if (output is null)
        {
            return;
        }

        var ffmpeg = DocsAlbum.FindFfmpeg()
            ?? throw new InvalidOperationException("生成文档截图需要 FFmpeg（PATH 中或程序目录的 tools/ 下）。");
        Directory.CreateDirectory(output);

        // 路径会出现在检查器与任务报告里，用中性的目录名
        var root = Path.Combine(Path.GetTempPath(), "LivePhotoConvert-Pictures");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        try
        {
            string? first = null;
            foreach (var language in Languages)
            {
                // 两种语言用同一份内容，只有相册目录名不同（目录名显示在标题栏与分组标题）
                var album = Path.Combine(root, language == "zh" ? "2026 夏日旅行" : "Summer Trip 2026");
                if (first is null)
                {
                    await DocsAlbum.WriteAsync(album, ffmpeg, Token);
                    first = album;
                }
                else
                {
                    DocsAlbum.CopyTo(first, album);
                }

                var converted = Path.Combine(root, "Converted");

                await CaptureWorkbenchAsync(output, language, album, converted);
                await CaptureLibraryAsync(output, language, ThemeService.Dark, album, converted);
                await CaptureToolsAsync(output, language);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留给系统临时目录清理
            }
        }
    }

    /// <summary>浅色主题下的一整轮：图库（带选中）、QuickLook、瘦身检查器、瘦身对比、任务报告、设置。</summary>
    private static async Task CaptureWorkbenchAsync(string output, string language, string album, string converted)
    {
        var runner = ScriptedRunner.Returning(SampleReport(album, converted));
        using var session = Open(language, ThemeService.Light, album, converted, services => services.AddSingleton<IConversionRunner>(runner));
        var library = session.Shell.Library;
        await ScanAsync(session, DocsAlbum.ApplePairCount);

        // 图库：选中第一天的三张
        library.Selection.SetSelected(library.AllCards.Where(c => c.FileName is "IMG_4102" or "IMG_4104" or "IMG_4105"), true);
        await SettleThumbnailsAsync(session);
        Save(session, output, $"library-light-{language}");

        // QuickLook：实况视频真实播放
        library.Selection.Clear();
        library.OpenQuickLookCommand.Execute(library.AllCards.Single(c => c.FileName == "IMG_4104"));
        var quickLook = Assert.IsType<QuickLookDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => quickLook.Player?.State.Status == PlayerStatus.Playing, 30);
        await PumpForAsync(session, TimeSpan.FromMilliseconds(600));
        Save(session, output, $"quicklook-{language}");
        session.PressEscape();
        await session.WaitUntilAsync(() => session.Dialogs.Current is null);

        // 瘦身：实况对与动态照片一起列出，等空间预估完成
        session.Shell.Inspector.SetActionCommand.Execute(nameof(ConversionAction.Strip));
        await session.WaitUntilAsync(() => library.AllCards.Count == DocsAlbum.Entries.Count);
        await session.WaitUntilAsync(() => session.Shell.Inspector is { IsEstimating: false, HasEstimate: true }, 120);
        await SettleThumbnailsAsync(session);
        Save(session, output, $"strip-{language}");

        // 瘦身对比：对选中的动态照片实际运行一次瘦身
        library.Selection.SetSelected(library.AllCards.Where(c => c.FileName == "MVIMG_20260720_183205"), true);
        session.Pump();
        session.Shell.Inspector.OpenStripCompareCommand.Execute(null);
        var compare = Assert.IsType<StripCompareDialogViewModel>(session.Dialogs.Current);
        await session.WaitUntilAsync(() => compare.StrippedCompareBitmap is not null && compare.DisplayTask.IsCompleted, 120);
        await PumpForAsync(session, TimeSpan.FromMilliseconds(400));
        await session.WaitUntilAsync(() => compare.DisplayTask.IsCompleted, 60);
        Save(session, output, $"strip-compare-{language}");
        session.PressEscape();
        await session.WaitUntilAsync(() => session.Dialogs.Current is null);
        library.Selection.Clear();

        // 任务报告：合成任务的结果（含一条时间不一致而跳过的配对）
        session.Shell.Inspector.SetActionCommand.Execute(nameof(ConversionAction.ToAndroid));
        session.Pump();
        var history = session.Host.Get<TaskCenter>().History;
        await session.Shell.Inspector.StartCommand.ExecuteAsync(null);
        await session.WaitUntilAsync(() => history.Count == 1, 30);
        session.Navigate(AppPage.Tasks);
        Save(session, output, $"report-{language}");

        session.Navigate(AppPage.Settings);
        Save(session, output, $"settings-{language}");
        session.Log.AssertNoBindingErrors();
    }

    private static async Task CaptureLibraryAsync(string output, string language, string theme, string album, string converted)
    {
        using var session = Open(language, theme, album, converted);
        var library = session.Shell.Library;
        await ScanAsync(session, DocsAlbum.ApplePairCount);
        library.Selection.SetSelected(library.AllCards.Where(c => c.FileName is "IMG_4103" or "IMG_4106"), true);
        await SettleThumbnailsAsync(session);
        Save(session, output, $"library-{theme.ToLowerInvariant()}-{language}");
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>依赖页：展示便携部署的典型状态——两个工具已就绪，heif-enc 正在下载。</summary>
    private static async Task CaptureToolsAsync(string output, string language)
    {
        const string installRoot = @"D:\LivePhotoConvert\tools";
        var registry = new FakeToolRegistry();
        registry.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, installRoot + @"\exiftool\exiftool.exe", "13.59", recommended: "13.59");
        registry.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, installRoot + @"\ffmpeg\ffmpeg.exe", "7.0",
            ToolCapabilities.Hdr | ToolCapabilities.Libx264, recommended: "7.0");
        registry.Infos[ToolId.HeifEnc] = FakeToolRegistry.Missing(ToolId.HeifEnc, "1.23.4");
        var installer = new PendingInstaller(installRoot);

        using var session = new ShellSession(language, ThemeService.Light, services =>
        {
            services.AddSingleton<IToolRegistry>(registry);
            services.AddSingleton<IToolInstaller>(installer);
        });
        session.Navigate(AppPage.Tools);
        var tools = session.Shell.Tools;
        await session.WaitUntilAsync(() => tools.ExifTool.IsReady && tools.Ffmpeg.IsReady);

        var install = tools.InstallAsync(ToolId.HeifEnc);
        await session.WaitUntilAsync(() => tools.HeifEnc.ProgressValue > 0);
        Save(session, output, $"tools-{language}");

        installer.Complete(ToolId.HeifEnc);
        await install.WaitAsync(TimeSpan.FromSeconds(10), Token);
        session.Pump();
        session.Log.AssertNoBindingErrors();
    }

    private static ShellSession Open(string language, string theme, string album, string converted, Action<IServiceCollection>? configure = null)
    {
        var session = new ShellSession(language, theme, configure, s =>
        {
            s.LastScanDirectory = album;
            s.OutputDirectory = converted;
            s.StripOutputDirectory = converted;
            s.Action = ConversionAction.ToAndroid;
            s.NotifyOnComplete = false;
            s.AutoOpenOutput = false;
            // 小行高：首屏能放下更多照片与第二个日期分组
            s.Gallery.Scale = "Small";
        });
        return session;
    }

    private static async Task ScanAsync(ShellSession session, int expected)
    {
        var library = session.Shell.Library;
        library.AlbumDirectory = session.Host.Settings.Current.LastScanDirectory;
        await library.RefreshAlbumAsync();
        await session.WaitUntilAsync(() => library.AllCards.Count == expected, 30);
    }

    /// <summary>缩略图在后台解码：等到已有卡片出图，且出图数量连续一段时间不再变化。</summary>
    private static async Task SettleThumbnailsAsync(ShellSession session)
    {
        var cards = session.Shell.Library.AllCards;
        await session.WaitUntilAsync(() => cards.Any(c => c.DisplayImage is not null), 30);
        var stableSince = DateTime.UtcNow;
        var last = -1;
        while (DateTime.UtcNow - stableSince < TimeSpan.FromMilliseconds(800))
        {
            await Task.Delay(50, Token);
            session.Pump();
            var loaded = cards.Count(c => c.DisplayImage is not null);
            if (loaded != last)
            {
                last = loaded;
                stableSince = DateTime.UtcNow;
            }
        }
    }

    private static async Task PumpForAsync(ShellSession session, TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(30, Token);
            session.Pump();
        }
    }

    private static void Save(ShellSession session, string directory, string name)
    {
        using var frame = session.Capture() ?? throw new InvalidOperationException($"截图 {name} 没有渲染出帧。");
        frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
        TestContext.Current.TestOutputHelper?.WriteLine($"截图: {name}.png");
    }

    /// <summary>合成任务的示例结果：一张写入了 Ultra HDR 封面，时间不一致的一对被跳过。</summary>
    private static BatchReport SampleReport(string album, string converted)
    {
        List<ItemOutcome> items = [];
        foreach (var entry in DocsAlbum.Entries.Where(e => !e.AndroidMotionPhoto))
        {
            var source = Path.Combine(album, entry.Stem + ".JPG");
            if (entry.TimeMismatch)
            {
                items.Add(ItemOutcome.Skipped(source, new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, 120d, 3d)));
                continue;
            }

            var outcome = ItemOutcome.Succeeded(source, Path.Combine(converted, $"MVIMG_{entry.Taken:yyyyMMdd_HHmmss}_{entry.Stem}.jpg"));
            items.Add(entry.Stem == "IMG_4104" ? outcome with { Notes = [new OutcomeNote(OutcomeNoteKind.UltraHdrWritten)] } : outcome);
        }

        return new BatchReport(items, TimeSpan.FromSeconds(11.6), Canceled: false);
    }

    /// <summary>环境变量中的目录；相对路径按仓库根目录解析。</summary>
    private static string? ResolveOutputDirectory()
    {
        var value = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Path.IsPathRooted(value))
        {
            return Path.GetFullPath(value);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LivePhotoConvert.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(directory?.FullName ?? Environment.CurrentDirectory, value));
    }

    /// <summary>安装停在"下载中"，直到截图后由 <see cref="Complete"/> 结束。</summary>
    private sealed class PendingInstaller(string installRoot) : IToolInstaller
    {
        private readonly Dictionary<ToolId, TaskCompletionSource<ToolInstallResult>> _pending = [];

        public ToolManifest Manifest => ToolManifest.Embedded;

        public string InstallRoot => installRoot;

        public IReadOnlyList<ToolDownloadCandidate> GetCandidates(ToolId tool, string? mirrorPrefix = null) =>
            [.. Manifest.Get(tool).Packages.Select(p => new ToolDownloadCandidate(p, new Uri(p.Url), null))];

        public Task<ToolInstallResult> InstallAsync(ToolId tool, string? mirrorPrefix = null, IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var package = Manifest.Get(tool).Packages[0];
            progress?.Report(new ToolInstallProgress(tool, ToolInstallStage.Downloading, new ToolDownloadCandidate(package, new Uri(package.Url), mirrorPrefix),
                1, 1, 5L << 20, 12L << 20, 2.4 * (1 << 20)));
            var pending = new TaskCompletionSource<ToolInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[tool] = pending;
            return pending.Task.WaitAsync(cancellationToken);
        }

        public void RecoverInterruptedInstalls()
        {
        }

        public void Complete(ToolId tool)
        {
            var definition = Manifest.Get(tool);
            var path = Path.Combine(installRoot, definition.InstallDirectory, definition.ExecutableFileName);
            _pending[tool].SetResult(new ToolInstallResult(tool, path, Path.Combine(installRoot, definition.InstallDirectory), "1.23.4", definition.Packages[0].Id));
        }
    }
}
