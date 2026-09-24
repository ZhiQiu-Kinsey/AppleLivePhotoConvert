using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoConvert.Core.External.Tools;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace LivePhotoConvert.Desktop.Features.Updates;

/// <summary>
/// 从 GitHub Releases 读取 Velopack 更新清单（releases.{channel}.json）与更新包，只取正式版。
/// </summary>
/// <remarks>
/// 信任链：发布列表只从 api.github.com 直连读取，其中每个附件的 SHA256 摘要是信任锚；经加速镜像取得的更新清单必须与摘要一致，
/// 清单中记录的 SHA256 再约束更新包。镜像因此只能拖慢或中断下载，无法替换内容；没有摘要的清单不经镜像下载。
/// 镜像仅在用户于依赖页选择了加速前缀时使用，先走镜像，失败或校验不符时改为直连。
/// </remarks>
internal sealed class GithubReleaseSource : IUpdateSource
{
    public const int ReleasesPerPage = 10;

    private const int BufferSize = 81920;
    private const long MaxFeedBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly Uri _releasesApi;
    private readonly HttpClient _http;
    private readonly Func<string?> _mirrorPrefix;
    private readonly Func<SemanticVersion?> _currentVersion;
    private readonly Lock _gate = new();
    private Dictionary<string, GithubAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ReleaseNotesEntry> _newerReleases = [];

    /// <param name="repositoryUrl">形如 https://github.com/owner/repo</param>
    /// <param name="http">不设总超时的客户端；单次请求的超时由本类控制</param>
    /// <param name="mirrorPrefix">用户设置的 GitHub 加速前缀；空白或直连时返回 null / 空串</param>
    /// <param name="currentVersion">当前版本；只读取比它新的发布</param>
    public GithubReleaseSource(string repositoryUrl, HttpClient http, Func<string?> mirrorPrefix, Func<SemanticVersion?> currentVersion)
    {
        var repository = new Uri(repositoryUrl.TrimEnd('/'));
        _releasesApi = new Uri($"https://api.github.com/repos{repository.AbsolutePath}/releases?per_page={ReleasesPerPage}");
        _http = http;
        _mirrorPrefix = mirrorPrefix;
        _currentVersion = currentVersion;
    }

    /// <summary>最近一次读取到的、比当前版本新的正式版及其说明（GitHub Release 正文），从新到旧。</summary>
    public IReadOnlyList<ReleaseNotesEntry> NewerReleases
    {
        get
        {
            lock (_gate)
            {
                return _newerReleases;
            }
        }
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var current = _currentVersion();
        var feedName = $"releases.{channel}.json";
        var releases = (await GetReleasesAsync())
            .Where(r => r is { Draft: false, Prerelease: false })
            .Select(r => (Release: r, Version: ParseTag(r.TagName)))
            .Where(r => r.Version is not null)
            .OrderByDescending(r => r.Version)
            .ToList();

        var assets = new Dictionary<string, GithubAsset>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<VelopackAsset>();
        var notes = new List<ReleaseNotesEntry>();
        foreach (var (release, version) in releases)
        {
            // 增量更新需要当前版本之后每个版本的增量包，它们分别位于各自的发布中
            if (current is not null && version! <= current)
            {
                break;
            }

            var feedAsset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, feedName, StringComparison.OrdinalIgnoreCase));
            if (feedAsset is null)
            {
                logger.Warn($"发布 {release.TagName} 中没有 {feedName}，跳过。");
                continue;
            }

            var feed = VelopackAssetFeed.FromJson(await DownloadFeedAsync(feedAsset));
            entries.AddRange(feed.Assets);
            foreach (var asset in release.Assets!.Where(a => !string.IsNullOrEmpty(a.Name)))
            {
                assets.TryAdd(asset.Name!, asset);
            }

            var body = string.IsNullOrWhiteSpace(release.Body)
                ? feed.Assets.FirstOrDefault(a => a.Type == VelopackAssetType.Full)?.NotesMarkdown ?? string.Empty
                : release.Body;
            notes.Add(new ReleaseNotesEntry(version!.ToString(), body.Trim()));
            if (current is null)
            {
                break;
            }
        }

        lock (_gate)
        {
            _assets = assets;
            _newerReleases = notes;
        }

        return new VelopackAssetFeed { Assets = [.. entries] };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        GithubAsset? asset;
        lock (_gate)
        {
            _assets.TryGetValue(releaseEntry.FileName, out asset);
        }

        if (asset?.BrowserDownloadUrl is not { } url)
        {
            throw new UpdateException(UpdateFailureKind.NotFound, $"发布中没有更新包 {releaseEntry.FileName}。");
        }

        if (string.IsNullOrEmpty(releaseEntry.SHA256))
        {
            throw new UpdateException(UpdateFailureKind.Integrity, $"更新清单没有记录 {releaseEntry.FileName} 的 SHA256。");
        }

        // 清单已经过直连摘要校验（或直连取得），其中的 SHA256 足以约束镜像下载的内容
        UpdateException? last = null;
        foreach (var candidate in Candidates(url, allowMirror: true))
        {
            try
            {
                await DownloadFileAsync(candidate, localFile, releaseEntry.Size, progress, cancelToken);
                var actual = await Sha256OfFileAsync(localFile, cancelToken);
                if (!actual.Equals(releaseEntry.SHA256, StringComparison.OrdinalIgnoreCase) || !MatchesDigest(asset.Digest, actual))
                {
                    throw new UpdateException(UpdateFailureKind.Integrity, $"{releaseEntry.FileName} 的 SHA256 与更新清单不符（{candidate.Host}）。");
                }

                return;
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                TryDelete(localFile);
                throw;
            }
            catch (Exception ex) when (Classify(ex, writesFile: true) is { } failure)
            {
                TryDelete(localFile);
                logger.Warn($"从 {candidate.Host} 下载 {releaseEntry.FileName} 失败：{ex.Message}");
                last = failure;
            }
        }

        throw last!;
    }

    /// <summary>把 tag（v1.2.3 / 1.2.3）解析为版本号；无法解析的 tag 不参与比较。</summary>
    internal static SemanticVersion? ParseTag(string? tag) =>
        SemanticVersion.TryParse(tag?.Trim().TrimStart('v', 'V'), out var version) ? version : null;

    /// <summary>GitHub 附件摘要形如 "sha256:hex"；没有摘要时不作约束。</summary>
    internal static bool MatchesDigest(string? digest, string sha256Hex)
    {
        const string Prefix = "sha256:";
        if (string.IsNullOrEmpty(digest))
        {
            return true;
        }

        return digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && digest.AsSpan(Prefix.Length).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    internal static HttpClient CreateDefaultClient(string userAgentVersion)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LivePhotoConvert/{userAgentVersion}");
        return client;
    }

    private async Task<List<GithubRelease>> GetReleasesAsync()
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsRateLimited(response))
            {
                throw new UpdateException(UpdateFailureKind.RateLimited, $"GitHub 接口限流（HTTP {(int)response.StatusCode}）。");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException(UpdateFailureKind.NotFound, "找不到发布列表（HTTP 404）。");
            }

            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.ListGithubRelease, timeout.Token) ?? [];
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateFailureKind.Unknown, "发布列表格式无法识别。", ex);
        }
        catch (Exception ex) when (ex is not UpdateException && Classify(ex, timeout.IsCancellationRequested) is { } failure)
        {
            throw failure;
        }
    }

    private async Task<string> DownloadFeedAsync(GithubAsset asset)
    {
        if (asset.BrowserDownloadUrl is not { } url)
        {
            throw new UpdateException(UpdateFailureKind.NotFound, $"{asset.Name} 没有下载地址。");
        }

        UpdateException? last = null;
        foreach (var candidate in Candidates(url, allowMirror: !string.IsNullOrEmpty(asset.Digest)))
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            try
            {
                using var response = await _http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxFeedBytes)
                {
                    throw new UpdateException(UpdateFailureKind.Integrity, $"{asset.Name} 超出大小上限。");
                }

                var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaxFeedBytes, timeout.Token);
                if (!MatchesDigest(asset.Digest, Convert.ToHexString(SHA256.HashData(bytes))))
                {
                    throw new UpdateException(UpdateFailureKind.Integrity, $"{asset.Name} 与 GitHub 记录的摘要不符（{candidate.Host}）。");
                }

                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');
            }
            catch (Exception ex) when (Classify(ex, timeout.IsCancellationRequested) is { } failure)
            {
                last = failure;
            }
        }

        throw last!;
    }

    private IEnumerable<Uri> Candidates(string githubUrl, bool allowMirror)
    {
        var mirror = allowMirror ? ToolDownloadUrls.NormalizeMirrorPrefix(_mirrorPrefix()) : null;
        if (mirror is not null)
        {
            yield return new Uri(ToolDownloadUrls.ApplyMirror(githubUrl, mirror));
        }

        yield return new Uri(ToolDownloadUrls.ApplyMirror(githubUrl, null));
    }

    private async Task DownloadFileAsync(Uri url, string localFile, long expectedSize, Action<int> progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 1, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long received = 0;
            var lastPercent = -1;
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                var read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                var percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress(percent);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            using var memory = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    if (memory.Length + read > limit)
                    {
                        throw new UpdateException(UpdateFailureKind.Integrity, "更新清单超出大小上限。");
                    }

                    memory.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return memory.ToArray();
        }
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(v => v.Trim() == "0"));

    /// <summary>把可重试或可说明的异常归类；返回 null 表示不认识的异常，原样抛出。</summary>
    /// <param name="ex">异常</param>
    /// <param name="timedOut">本类设置的请求超时已到</param>
    /// <param name="writesFile">操作包含写本地文件；否则 I/O 错误都来自网络连接</param>
    private static UpdateException? Classify(Exception ex, bool timedOut = false, bool writesFile = false) => ex switch
    {
        UpdateException update => update,
        HttpRequestException http => new UpdateException(UpdateFailureKind.Network, http.Message, http),
        OperationCanceledException canceled when timedOut || canceled.InnerException is TimeoutException =>
            new UpdateException(UpdateFailureKind.Network, "请求超时。", canceled),
        // 取消来自空闲超时（调用方未取消）时同样视为网络问题
        OperationCanceledException canceled when canceled.CancellationToken.IsCancellationRequested && !timedOut =>
            new UpdateException(UpdateFailureKind.Network, "连接长时间没有数据。", canceled),
        IOException io when !writesFile || io is HttpIOException || io.InnerException is System.Net.Sockets.SocketException =>
            new UpdateException(UpdateFailureKind.Network, io.Message, io),
        IOException io => new UpdateException(UpdateFailureKind.Disk, io.Message, io),
        UnauthorizedAccessException denied => new UpdateException(UpdateFailureKind.Disk, denied.Message, denied),
        _ => null
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Velopack 下次下载前会覆盖同名的未完成文件
        }
    }
}

/// <summary>一个版本的更新说明。</summary>
public sealed record ReleaseNotesEntry(string Version, string NotesMarkdown);

internal sealed class GithubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GithubAsset>? Assets { get; set; }
}

internal sealed class GithubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>GitHub 在上传时计算的摘要，形如 "sha256:hex"。</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSerializable(typeof(List<GithubRelease>))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
