using System.Net;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>按预设返回探测结果的注册表，记录每次失效。</summary>
public sealed class FakeToolRegistry : IToolRegistry
{
    public Dictionary<ToolId, ToolInfo> Infos { get; } = [];

    public List<ToolId?> Invalidations { get; } = [];

    public event EventHandler<ToolId?>? Invalidated;

    public static ToolInfo Missing(ToolId tool, string? recommended = "1.0", bool explicitInvalid = false) =>
        new(tool, null, null, null, ToolCapabilities.None, recommended, explicitInvalid, null);

    public static ToolInfo Found(ToolId tool, string path, string versionText, ToolCapabilities capabilities = ToolCapabilities.None,
        string? recommended = null, bool explicitInvalid = false, string? probeError = null) =>
        new(tool, path, versionText, Version.TryParse(versionText.Split('-')[0].TrimStart('n'), out var v) ? v : null,
            capabilities, recommended, explicitInvalid, probeError);

    public Task<ToolInfo> GetAsync(ToolId tool, CancellationToken cancellationToken = default) =>
        Task.FromResult(Infos.TryGetValue(tool, out var info) ? info : Missing(tool));

    public void Invalidate(ToolId? tool = null)
    {
        Invalidations.Add(tool);
        Invalidated?.Invoke(this, tool);
    }
}

/// <summary>不联网的安装器：安装过程由测试脚本决定。</summary>
public sealed class FakeToolInstaller(bool platformSupported = true) : IToolInstaller
{
    public delegate Task<ToolInstallResult> InstallScript(ToolId tool, string? mirror, IProgress<ToolInstallProgress>? progress, CancellationToken cancellationToken);

    public ToolManifest Manifest => ToolManifest.Embedded;

    public string InstallRoot { get; } = Path.Combine(Path.GetTempPath(), "lpc-fake-tools");

    public List<(ToolId Tool, string? Mirror)> Calls { get; } = [];

    public int RecoverCount { get; private set; }

    public InstallScript? Script { get; set; }

    /// <summary>忽略运行时标识：测试机不是 win-x64 也要有下载源可用。</summary>
    public IReadOnlyList<ToolDownloadCandidate> GetCandidates(ToolId tool, string? mirrorPrefix = null) =>
        platformSupported
            ? [.. Manifest.Get(tool).Packages.Select(p => new ToolDownloadCandidate(p, new Uri(p.Url), null))]
            : [];

    public Task<ToolInstallResult> InstallAsync(ToolId tool, string? mirrorPrefix = null, IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((tool, mirrorPrefix));
        return Script is { } script ? script(tool, mirrorPrefix, progress, cancellationToken) : Task.FromResult(Result(tool, ExecutablePath(tool)));
    }

    public void RecoverInterruptedInstalls() => RecoverCount++;

    public string ExecutablePath(ToolId tool) =>
        Path.Combine(InstallRoot, Manifest.Get(tool).InstallDirectory, Manifest.Get(tool).ExecutableFileName);

    public static ToolInstallResult Result(ToolId tool, string path) =>
        new(tool, path, Path.GetDirectoryName(path)!, "7.0", "test");

    /// <summary>清单中第一个下载源的一次尝试。</summary>
    public ToolDownloadCandidate Candidate(ToolId tool, string? mirror = null)
    {
        var package = Manifest.Get(tool).Packages[0];
        return new ToolDownloadCandidate(package, new Uri(package.Url), mirror);
    }
}

/// <summary>可控的任务占用状态，记录释放调用。</summary>
public sealed class FakeToolUsage : IToolUsage
{
    public bool IsBusy
    {
        get;
        set
        {
            field = value;
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public List<ToolId> Released { get; } = [];

    public event EventHandler? BusyChanged;

    /// <summary>释放完成的时机；默认立即完成。</summary>
    public Func<ToolId, Task> ReleaseCompletion { get; set; } = _ => Task.CompletedTask;

    public Task ReleaseIdleProcessesAsync(ToolId tool)
    {
        Released.Add(tool);
        return ReleaseCompletion(tool);
    }
}

/// <summary>按请求返回预设响应的处理器。</summary>
public sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Uri?> Requests { get; } = [];

    public static ScriptedHandler Status(HttpStatusCode status) => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri);
        return respond(request, cancellationToken);
    }
}

/// <summary>
/// 用假注册表、假安装器与可控时钟组装依赖页：界面投递同步执行，设置写到临时目录。
/// </summary>
public sealed class ToolsFixture : IDisposable
{
    private readonly CultureScope _culture = new();

    public ToolsFixture(bool platformSupported = true, HttpMessageHandler? http = null, Action<FakeToolRegistry>? arrange = null, bool usageBusy = false)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"lpc_tools_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        Settings = new SettingsStore(Path.Combine(Directory, "settings.json"));
        Paths = new ToolPathSettings(Settings);
        Paths.InvalidateOnChange(Registry);
        Installer = new FakeToolInstaller(platformSupported);
        Usage.IsBusy = usageBusy;
        arrange?.Invoke(Registry);
        Http = http ?? ScriptedHandler.Status(HttpStatusCode.OK);
        ViewModel = new ToolsViewModel(Settings, Paths, Localizer, FilePicker, Registry, Installer, Usage, Time, action => action(), new HttpClient(Http));
    }

    public string Directory { get; }

    public SettingsStore Settings { get; }

    public ToolPathSettings Paths { get; }

    public Localizer Localizer { get; } = new();

    public FakeFilePicker FilePicker { get; } = new();

    public FakeToolRegistry Registry { get; } = new();

    public FakeToolInstaller Installer { get; }

    public FakeToolUsage Usage { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public HttpMessageHandler Http { get; }

    public ToolsViewModel ViewModel { get; }

    public string CreateFile(string name, string content = "stub")
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Settings.Dispose();
        _culture.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响测试结果
        }
    }
}
