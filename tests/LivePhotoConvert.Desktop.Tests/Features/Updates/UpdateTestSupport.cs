using System.Net;
using System.Security.Cryptography;
using System.Text;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Tests.Features.Updates;

/// <summary>不联网、可控的更新服务：检查结果、下载进度与失败都由测试决定，并记录安装调用。</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public bool IsSupported { get; set; } = true;

    public string CurrentVersion { get; set; } = "3.0.3";

    public AvailableUpdate? NextUpdate { get; set; }

    public Exception? CheckFailure { get; set; }

    public Exception? DownloadFailure { get; set; }

    /// <summary>设置后下载在报告第一次进度后停住，直到放行或取消。</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public int CheckCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public int ApplyAndRestartCalls { get; private set; }

    public int ApplyOnExitCalls { get; private set; }

    public int OnExitingCalls { get; private set; }

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        CheckCalls++;
        return CheckFailure is { } failure ? Task.FromException<AvailableUpdate?>(failure) : Task.FromResult(NextUpdate);
    }

    public async Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        DownloadCalls++;
        progress?.Report(42);
        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (DownloadFailure is { } failure)
        {
            throw failure;
        }

        progress?.Report(100);
    }

    public Exception? ApplyAndRestartFailure { get; set; }

    public void ApplyAndRestart()
    {
        ApplyAndRestartCalls++;
        if (ApplyAndRestartFailure is { } failure)
        {
            throw failure;
        }
    }

    public void ApplyOnExit() => ApplyOnExitCalls++;

    public void OnExiting() => OnExitingCalls++;
}

/// <summary>可随时切换忙碌状态的后台任务；取消即结束。</summary>
internal sealed class FakeBackgroundWork : IBackgroundWork
{
    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int CancelCalls { get; private set; }

    public event EventHandler? BusyChanged;

    public Task CancelAndWaitAsync(TimeSpan timeout)
    {
        CancelCalls++;
        IsBusy = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAppShutdown : IAppShutdown
{
    public bool Result { get; set; } = true;

    public int Calls { get; private set; }

    public Task<bool> ShutdownForUpdateAsync()
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

/// <summary>按请求地址返回预设响应的 HTTP 处理器；未登记的地址返回 404，并记录全部请求。</summary>
internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public void Map(string url, Func<HttpResponseMessage> response) => _routes[url] = response;

    public void MapBytes(string url, byte[] content) => Map(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

    public void MapStatus(string url, HttpStatusCode status) => Map(url, () => new HttpResponseMessage(status));

    /// <summary>按登记的路由应答但不记录请求（模拟镜像转发）。</summary>
    public HttpResponseMessage Respond(string url) =>
        _routes.TryGetValue(url, out var response) ? response() : new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests)
        {
            Requests.Add(url);
        }

        return Task.FromResult(Respond(url));
    }
}

/// <summary>模拟 GitHub 上的发布：发布列表 JSON、每个发布的 releases.win.json 与更新包。</summary>
internal sealed class FakeGithubReleases
{
    public const string Repository = "https://github.com/ZhiQiu-Kinsey/AppleLivePhotoConvert";
    public const string ReleasesApi = "https://api.github.com/repos/ZhiQiu-Kinsey/AppleLivePhotoConvert/releases?per_page=10";
    public const string PackId = "LivePhotoConvert.App";

    private readonly List<string> _releaseJson = [];

    public RoutingHandler Handler { get; } = new();

    public static string DownloadUrl(string tag, string file) => $"{Repository}/releases/download/{tag}/{file}";

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>登记一个带 Velopack 资产的发布，返回完整包内容。</summary>
    /// <param name="withDigest">发布列表中是否带附件摘要</param>
    /// <param name="feedDigestOverride">写入发布列表的清单摘要（模拟被篡改的清单）</param>
    public byte[] AddRelease(string version, string notes, bool prerelease = false, bool withDigest = true, string? feedDigestOverride = null, int packageSize = 2048)
    {
        var tag = "v" + version;
        var package = new byte[packageSize];
        new Random(version.GetHashCode()).NextBytes(package);
        var packageName = $"{PackId}-{version}-full.nupkg";
        var feed = Encoding.UTF8.GetBytes(
            $$"""{"Assets":[{"PackageId":"{{PackId}}","Version":"{{version}}","Type":"Full","FileName":"{{packageName}}","SHA1":"","SHA256":"{{Sha256(package)}}","Size":{{package.Length}},"NotesMarkdown":"feed notes {{version}}"}]}""");
        Handler.MapBytes(DownloadUrl(tag, "releases.win.json"), feed);
        Handler.MapBytes(DownloadUrl(tag, packageName), package);

        var feedDigest = feedDigestOverride ?? "sha256:" + Sha256(feed).ToLowerInvariant();
        var packageDigest = "sha256:" + Sha256(package).ToLowerInvariant();
        _releaseJson.Add($$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"body":{{System.Text.Json.JsonSerializer.Serialize(notes)}},
             "assets":[
               {"name":"releases.win.json","browser_download_url":"{{DownloadUrl(tag, "releases.win.json")}}","size":{{feed.Length}}{{(withDigest ? $",\"digest\":\"{feedDigest}\"" : "")}}},
               {"name":"{{packageName}}","browser_download_url":"{{DownloadUrl(tag, packageName)}}","size":{{package.Length}}{{(withDigest ? $",\"digest\":\"{packageDigest}\"" : "")}}}
             ]}
            """);
        Publish();
        return package;
    }

    /// <summary>没有 Velopack 资产的旧发布（只有 ZIP）。</summary>
    public void AddLegacyRelease(string version)
    {
        _releaseJson.Add($$"""{"tag_name":"v{{version}}","draft":false,"prerelease":false,"body":"old","assets":[{"name":"LivePhotoConvert-v{{version}}-win-x64.zip","browser_download_url":"{{DownloadUrl("v" + version, "x.zip")}}","size":1}]}""");
        Publish();
    }

    private void Publish()
    {
        var json = "[" + string.Join(",", Enumerable.Reverse(_releaseJson)) + "]";
        Handler.MapBytes(ReleasesApi, Encoding.UTF8.GetBytes(json));
    }
}
