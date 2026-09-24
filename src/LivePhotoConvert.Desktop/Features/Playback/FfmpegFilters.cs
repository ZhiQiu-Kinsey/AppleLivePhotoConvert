using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// FFmpeg 可用滤镜探测（<c>ffmpeg -hide_banner -filters</c>）。
/// </summary>
/// <remarks>结果按可执行文件路径、大小与修改时间缓存，替换 FFmpeg 后自动重新探测；探测失败不缓存。</remarks>
public static partial class FfmpegFilters
{
    public const string ZScale = "zscale";
    public const string ToneMap = "tonemap";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime LastWrite), Lazy<Task<FrozenSet<string>>>> Cache = new();

    /// <summary>HDR 预览所需的滤镜是否齐全。</summary>
    public static bool SupportsHdrToneMapping(IReadOnlySet<string> filters) => filters.Contains(ZScale) && filters.Contains(ToneMap);

    /// <summary>列出可用滤镜；无法运行 FFmpeg 时返回空集合。</summary>
    public static async Task<IReadOnlySet<string>> GetAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(ffmpegPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return FrozenSet<string>.Empty;
        }

        var key = (info.FullName, info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : default);
        var entry = Cache.GetOrAdd(key, static k => new Lazy<Task<FrozenSet<string>>>(() => ListAsync(k.Path)));
        // 共享的探测任务不随单个调用方取消
        var filters = await entry.Value.WaitAsync(cancellationToken);
        if (filters.Count == 0)
        {
            Cache.TryRemove(new KeyValuePair<(string, long, DateTime), Lazy<Task<FrozenSet<string>>>>(key, entry));
        }

        return filters;
    }

    /// <summary>解析 <c>-filters</c> 输出：每行为 3 位能力标记、滤镜名与输入输出类型。</summary>
    public static FrozenSet<string> Parse(string standardOutput)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var line in standardOutput.ReplaceLineEndings("\n").Split('\n'))
        {
            if (FilterLineRegex().Match(line) is { Success: true } match)
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    private static async Task<FrozenSet<string>> ListAsync(string ffmpegPath)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpegPath, ["-nostdin", "-hide_banner", "-filters"], CancellationToken.None, ProbeTimeout);
            return Parse(result.StandardOutput);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            return FrozenSet<string>.Empty;
        }
    }

    [GeneratedRegex(@"^\s*[T.][S.][C.]\s+([A-Za-z0-9_]+)\s+\S+->\S+", RegexOptions.CultureInvariant)]
    private static partial Regex FilterLineRegex();
}
