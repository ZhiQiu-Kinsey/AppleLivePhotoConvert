using System.Buffers;
using System.Diagnostics;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>连续一段时间没有收到任何数据。</summary>
public sealed class ToolDownloadStalledException(string message) : TimeoutException(message);

/// <summary>
/// 流式下载并同步计算哈希。
/// </summary>
/// <remarks>
/// 只设空闲超时而不设总超时：大包在慢速网络上可能要下很久，但持续没有数据说明连接已卡死。
/// </remarks>
internal sealed class ToolPackageDownloader(HttpClient client, TimeSpan idleTimeout)
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(150);

    /// <param name="onProgress">(已下载字节, 总字节, 即时速度)</param>
    /// <exception cref="ToolIntegrityException">哈希不匹配</exception>
    /// <exception cref="ToolDownloadStalledException">空闲超时</exception>
    public async Task DownloadAsync(Uri url, ToolPackage package, string destinationPath, Action<long, long?, double>? onProgress, CancellationToken cancellationToken)
    {
        using var integrity = new ToolIntegrity(package);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            idle.CancelAfter(idleTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long received = 0;
                long lastReportBytes = 0;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                onProgress?.Invoke(0, totalBytes, 0);
                while (true)
                {
                    idle.CancelAfter(idleTimeout);
                    var read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    integrity.Append(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;

                    var elapsed = clock.Elapsed;
                    if (elapsed - lastReport >= ReportInterval)
                    {
                        var speed = (received - lastReportBytes) / (elapsed - lastReport).TotalSeconds;
                        lastReport = elapsed;
                        lastReportBytes = received;
                        onProgress?.Invoke(received, totalBytes, speed);
                    }
                }

                if (totalBytes is { } expected && received != expected)
                {
                    throw new IOException($"下载不完整：收到 {received} 字节，应为 {expected} 字节。");
                }

                onProgress?.Invoke(received, totalBytes, 0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new ToolDownloadStalledException($"{idleTimeout.TotalSeconds:F0} 秒内没有收到数据，连接可能已卡住。");
        }

        integrity.Verify();
    }

    /// <summary>默认客户端：不自动解压（哈希针对原始字节），不设总超时（由空闲超时兜底）。</summary>
    public static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // 不伪装浏览器：SourceForge 对浏览器 UA 返回带跳转脚本的 HTML 页面而不是文件
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LivePhotoConvert/{typeof(ToolPackageDownloader).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return client;
    }
}
