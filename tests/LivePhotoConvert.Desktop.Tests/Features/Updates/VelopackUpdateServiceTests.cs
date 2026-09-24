using System.Net;
using LivePhotoConvert.Core.Tests.Support;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Models;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace LivePhotoConvert.Desktop.Tests.Features.Updates;

/// <summary>
/// Velopack 更新服务 + GitHub 发布源：用 Velopack 自带的测试定位器模拟"已安装 3.0.3"，HTTP 由内存路由应答，不联网。
/// </summary>
public sealed class VelopackUpdateServiceTests : IDisposable
{
    private const string Mirror = "https://mirror.example/";

    private readonly TempDirectory _temp = new();
    private readonly FakeGithubReleases _github = new();
    private string? _mirror;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WithoutVelopackInstall_IsNotSupported_AndDoesNotTouchNetwork()
    {
        // 测试进程没有执行 VelopackApp.Run()，与源码运行、旧版解压包相同
        var service = new VelopackUpdateService(() => null, http: new HttpClient(_github.Handler));

        Assert.False(service.IsSupported);
        Assert.Equal(AboutInfo.AppVersion, service.CurrentVersion);
        Assert.Empty(_github.Handler.Requests);
    }

    [Fact]
    public async Task Check_ReturnsNewestStableRelease_WithMergedNotesAndSize()
    {
        _github.AddLegacyRelease("3.0.2");
        _github.AddRelease("3.0.3", "current");
        _github.AddRelease("3.1.0", "### 新增\n- 自动更新");
        var package = _github.AddRelease("3.2.0", "### 修复\n- 小问题");
        _github.AddRelease("3.3.0-beta.1", "beta", prerelease: true);
        var service = CreateService();

        Assert.True(service.IsSupported);
        Assert.Equal("3.0.3", service.CurrentVersion);
        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("3.2.0", update.Version);
        Assert.Equal(package.Length, update.DownloadBytes);
        Assert.False(update.IsDelta);
        // 跨版本时两个版本的说明都在，新版本在前
        Assert.True(update.NotesMarkdown.IndexOf("## 3.2.0", StringComparison.Ordinal) < update.NotesMarkdown.IndexOf("## 3.1.0", StringComparison.Ordinal));
        Assert.Contains("自动更新", update.NotesMarkdown);
        Assert.DoesNotContain("beta", update.NotesMarkdown);
        // 不下载当前及更早版本的清单，也不碰预发布
        Assert.DoesNotContain(_github.Handler.Requests, r => r.Contains("/v3.0.3/", StringComparison.Ordinal) || r.Contains("beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_WhenUpToDate_ReturnsNull()
    {
        _github.AddRelease("3.0.3", "current");
        _github.AddRelease("3.0.4-rc.1", "rc", prerelease: true);

        Assert.Null(await CreateService().CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_ViaMirror_VerifiesAndStoresPackage()
    {
        var package = _github.AddRelease("3.1.0", "notes");
        MirrorAllGithubRoutes();
        _mirror = Mirror;
        var service = CreateService();
        var update = await service.CheckAsync(TestContext.Current.CancellationToken);
        var progress = new List<int>();

        await service.DownloadAsync(update!, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(package, File.ReadAllBytes(Path.Combine(PackagesDir, $"{FakeGithubReleases.PackId}-3.1.0-full.nupkg")));
        Assert.Contains(100, progress);
        // 发布列表只直连，清单与更新包先走镜像
        Assert.Contains(_github.Handler.Requests, r => r == FakeGithubReleases.ReleasesApi);
        Assert.Contains(_github.Handler.Requests, r => r.StartsWith(Mirror, StringComparison.Ordinal) && r.EndsWith("releases.win.json", StringComparison.Ordinal));
        Assert.Contains(_github.Handler.Requests, r => r.StartsWith(Mirror, StringComparison.Ordinal) && r.EndsWith(".nupkg", StringComparison.Ordinal));
        Assert.DoesNotContain(_github.Handler.Requests, r => r.StartsWith(FakeGithubReleases.Repository, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TamperedMirror_FallsBackToDirect_ForFeedAndPackage()
    {
        var package = _github.AddRelease("3.1.0", "notes");
        MirrorAllGithubRoutes();
        // 镜像返回被篡改的清单与更新包；摘要与清单里的哈希都对不上，必须改走直连
        _github.Handler.MapBytes(Mirror + FakeGithubReleases.DownloadUrl("v3.1.0", "releases.win.json"),
            """{"Assets":[{"PackageId":"LivePhotoConvert.App","Version":"9.9.9","Type":"Full","FileName":"evil.nupkg","SHA256":"00","Size":1}]}"""u8.ToArray());
        _github.Handler.MapBytes(Mirror + FakeGithubReleases.DownloadUrl("v3.1.0", $"{FakeGithubReleases.PackId}-3.1.0-full.nupkg"), [1, 2, 3]);
        _mirror = Mirror;
        var service = CreateService();

        var update = await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal("3.1.0", update!.Version);
        await service.DownloadAsync(update, null, TestContext.Current.CancellationToken);

        Assert.Equal(package, File.ReadAllBytes(Path.Combine(PackagesDir, $"{FakeGithubReleases.PackId}-3.1.0-full.nupkg")));
        Assert.Contains(_github.Handler.Requests, r => r == FakeGithubReleases.DownloadUrl("v3.1.0", "releases.win.json"));
    }

    [Fact]
    public async Task FeedWithoutDigest_IsNeverFetchedThroughMirror()
    {
        _github.AddRelease("3.1.0", "notes", withDigest: false);
        MirrorAllGithubRoutes();
        _mirror = Mirror;

        Assert.NotNull(await CreateService().CheckAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_github.Handler.Requests, r => r.StartsWith(Mirror, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FeedNotMatchingDigest_IsIntegrityFailure()
    {
        _github.AddRelease("3.1.0", "notes", feedDigestOverride: "sha256:" + new string('0', 64));

        var failure = await Assert.ThrowsAsync<UpdateException>(() => CreateService().CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailureKind.Integrity, failure.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true, UpdateFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, false, UpdateFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, false, UpdateFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, false, UpdateFailureKind.Network)]
    public async Task ReleaseListErrors_AreClassified(HttpStatusCode status, bool rateLimitHeader, UpdateFailureKind expected)
    {
        _github.Handler.Map(FakeGithubReleases.ReleasesApi, () =>
        {
            var response = new HttpResponseMessage(status);
            if (rateLimitHeader)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
            }

            return response;
        });

        var failure = await Assert.ThrowsAsync<UpdateException>(() => CreateService().CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, failure.Kind);
    }

    [Fact]
    public async Task NetworkFailure_IsClassifiedAsNetwork()
    {
        _github.Handler.Map(FakeGithubReleases.ReleasesApi, () => throw new HttpRequestException("connection refused"));

        var failure = await Assert.ThrowsAsync<UpdateException>(() => CreateService().CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(UpdateFailureKind.Network, failure.Kind);
    }

    [Fact]
    public async Task Download_ChecksumMismatchEverywhere_IsIntegrityFailure_AndLeavesNoPackage()
    {
        _github.AddRelease("3.1.0", "notes");
        _github.Handler.MapBytes(FakeGithubReleases.DownloadUrl("v3.1.0", $"{FakeGithubReleases.PackId}-3.1.0-full.nupkg"), [9, 9, 9]);
        var service = CreateService();
        var update = await service.CheckAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(update!, null, TestContext.Current.CancellationToken));

        Assert.Equal(UpdateFailureKind.Integrity, failure.Kind);
        Assert.Empty(Directory.EnumerateFiles(PackagesDir, "*.nupkg"));
    }

    [Fact]
    public async Task Download_RequiresTheCheckedVersion()
    {
        _github.AddRelease("3.1.0", "notes");
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(new AvailableUpdate("9.0.0", "", 1, false), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UpdaterFailingToStart_IsRetriedSilentlyOnExit()
    {
        var updateExe = _temp.Combine("Update.exe");
        var process = new RecordingProcess();
        var downloaded = new VelopackAsset
        {
            PackageId = FakeGithubReleases.PackId,
            Version = SemanticVersion.Parse("3.1.0"),
            Type = VelopackAssetType.Full,
            FileName = $"{FakeGithubReleases.PackId}-3.1.0-full.nupkg"
        };
        var locator = new RecordingLocator(PackagesDir, _temp.Root, updateExe, downloaded, process);
        var service = new VelopackUpdateService(() => null, locator, new HttpClient(_github.Handler));

        // Update.exe 缺失：立即重启失败
        Assert.Throws<FileNotFoundException>(service.ApplyAndRestart);
        Assert.Empty(process.Started);

        // 调用方随后改为退出时安装；更新程序恢复后，退出收尾必须真的启动它
        File.WriteAllBytes(updateExe, []);
        service.ApplyOnExit();
        service.OnExiting();

        var args = Assert.Single(process.Started);
        Assert.Contains("--silent", args);
        Assert.Contains("--norestart", args);
    }

    [Fact]
    public void ComposeNotes_SingleRelease_HasNoVersionHeading()
    {
        Assert.Equal("- a", VelopackUpdateService.ComposeNotes([new ReleaseNotesEntry("3.1.0", "- a")], "fallback"));
        Assert.Equal("fallback", VelopackUpdateService.ComposeNotes([new ReleaseNotesEntry("3.1.0", " ")], "fallback"));
    }

    [Theory]
    [InlineData(null, "ABCD", true)]
    [InlineData("sha256:abcd", "ABCD", true)]
    [InlineData("SHA256:ABCD", "abcd", true)]
    [InlineData("sha256:abce", "ABCD", false)]
    [InlineData("md5:abcd", "ABCD", false)]
    public void Digest_IsComparedCaseInsensitively(string? digest, string sha, bool expected) =>
        Assert.Equal(expected, GithubReleaseSource.MatchesDigest(digest, sha));

    [Theory]
    [InlineData("v3.1.0", "3.1.0")]
    [InlineData("3.1.0", "3.1.0")]
    [InlineData("v3.1.0-beta.1", "3.1.0-beta.1")]
    [InlineData("release-3", null)]
    public void Tags_AreParsedAsVersions(string tag, string? expected) =>
        Assert.Equal(expected, GithubReleaseSource.ParseTag(tag)?.ToNormalizedString());

    private string PackagesDir
    {
        get
        {
            var path = _temp.Combine("packages");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private VelopackUpdateService CreateService()
    {
        var locator = new TestVelopackLocator(FakeGithubReleases.PackId, "3.0.3", PackagesDir, null, null, null, channel: "win", logger: new NullVelopackLogger());
        return new VelopackUpdateService(() => _mirror, locator, new HttpClient(_github.Handler));
    }

    /// <summary>镜像默认如实转发：把已登记的 GitHub 下载地址同样登记到镜像前缀下。</summary>
    private void MirrorAllGithubRoutes()
    {
        foreach (var tag in new[] { "v3.1.0", "v3.2.0" })
        {
            foreach (var file in new[] { "releases.win.json", $"{FakeGithubReleases.PackId}-{tag[1..]}-full.nupkg" })
            {
                var direct = FakeGithubReleases.DownloadUrl(tag, file);
                _github.Handler.Map(Mirror + direct, () => _github.Handler.Respond(direct));
            }
        }
    }

    /// <summary>带本地已下载更新包与更新程序路径的定位器；启动进程只记录参数。</summary>
    private sealed class RecordingLocator(string packagesDir, string rootDir, string updateExe, VelopackAsset localPackage, RecordingProcess process)
        : TestVelopackLocator(FakeGithubReleases.PackId, "3.0.3", packagesDir, null, rootDir, updateExe, channel: "win",
            logger: new NullVelopackLogger(), localPackage: localPackage)
    {
        public override IProcessImpl Process => process;
    }

    private sealed class RecordingProcess : IProcessImpl
    {
        public List<string[]> Started { get; } = [];

        public string GetCurrentProcessPath() => Environment.ProcessPath ?? string.Empty;

        public uint GetCurrentProcessId() => (uint)Environment.ProcessId;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow) => Started.Add([.. args]);

        public void Exit(int exitCode) => throw new InvalidOperationException("测试中不应退出进程。");
    }

    /// <summary>同步回报的进度（Progress&lt;T&gt; 经线程池异步回报，断言时可能尚未到达）。</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
