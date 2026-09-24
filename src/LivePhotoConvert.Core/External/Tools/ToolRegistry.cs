namespace LivePhotoConvert.Core.External.Tools;

/// <summary>工具能力（目前只探测 FFmpeg）。</summary>
[Flags]
public enum ToolCapabilities
{
    None = 0,

    /// <summary>zscale 滤镜（libzimg），HDR 色调映射链需要。</summary>
    Zscale = 1 << 0,

    /// <summary>tonemap 滤镜。</summary>
    Tonemap = 1 << 1,

    Libx264 = 1 << 2,
    Libx265 = 1 << 3,

    /// <summary>libx265 支持 yuv420p10le 输出，HDR 转码保真需要。</summary>
    Libx265TenBit = 1 << 4,

    /// <summary>HDR 预览与转码所需的全部能力。</summary>
    Hdr = Zscale | Tonemap | Libx265 | Libx265TenBit
}

/// <summary>一次探测的结果。</summary>
/// <param name="Tool">工具</param>
/// <param name="Path">可执行文件完整路径；未找到时为 null</param>
/// <param name="VersionText">工具报告的版本文本</param>
/// <param name="Version">数字版本；无法识别（如 master 构建）时为 null</param>
/// <param name="Capabilities">能力</param>
/// <param name="RecommendedVersion">清单为当前平台推荐的版本；当前平台没有下载包时为 null</param>
/// <param name="IsExplicitPathInvalid">设置里指定了路径但该路径不可用（已回退到自动发现）</param>
/// <param name="ProbeError">找到了文件但读取版本或能力失败的原因</param>
public sealed record ToolInfo(
    ToolId Tool,
    string? Path,
    string? VersionText,
    Version? Version,
    ToolCapabilities Capabilities,
    string? RecommendedVersion,
    bool IsExplicitPathInvalid,
    string? ProbeError)
{
    public bool IsAvailable => Path is not null;

    public bool Has(ToolCapabilities capabilities) => (Capabilities & capabilities) == capabilities;

    /// <summary>已安装版本低于推荐版本。</summary>
    public bool IsUpdateRecommended =>
        Version is not null && ToolOutputParser.ParseVersion(RecommendedVersion) is { } recommended && Version < recommended;
}

/// <summary>探测参数。</summary>
public sealed class ToolRegistryOptions
{
    /// <summary>单条探测命令的超时，超时即结束进程。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>确定推荐版本所用的运行时标识。</summary>
    public string RuntimeIdentifier { get; init; } = ToolRuntime.CurrentRid;

    /// <summary>替换定位逻辑：(工具, 设置中的路径) → 可执行文件路径。</summary>
    internal Func<ToolDefinition, string?, string?>? Locator { get; init; }

    /// <summary>替换进程执行，用于验证缓存与解析而不启动真实工具。</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? Runner { get; init; }
}

/// <summary>
/// 解析并缓存工具路径、版本与能力。
/// </summary>
/// <remarks>
/// 每个工具只探测一次，并发调用共享同一次探测；设置中的路径变化或安装完成后调用 <see cref="Invalidate"/>。
/// 探测使用内部超时而不是调用方的取消令牌，调用方取消只是不再等待，不会让其他等待者拿到半截结果。
/// </remarks>
/// <param name="explicitPathProvider">读取设置中为工具指定的路径（空白表示自动发现）；每次探测时调用以取得最新值</param>
public sealed class ToolRegistry(Func<ToolId, string?>? explicitPathProvider = null, ToolManifest? manifest = null, ToolRegistryOptions? options = null) : IToolRegistry
{
    private readonly ToolManifest _manifest = manifest ?? ToolManifest.Embedded;
    private readonly ToolRegistryOptions _options = options ?? new ToolRegistryOptions();
    private readonly Lock _lock = new();
    private readonly Dictionary<ToolId, Task<ToolInfo>> _cache = [];

    /// <summary>缓存失效时触发；参数为失效的工具，null 表示全部。</summary>
    public event EventHandler<ToolId?>? Invalidated;

    public Task<ToolInfo> GetAsync(ToolId tool, CancellationToken cancellationToken = default)
    {
        Task<ToolInfo> probe;
        lock (_lock)
        {
            if (!_cache.TryGetValue(tool, out probe!))
            {
                probe = Task.Run(() => ProbeAsync(tool));
                _cache[tool] = probe;
            }
        }

        return probe.WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await Task.WhenAll(_manifest.Tools.Select(tool => GetAsync(tool.Id, cancellationToken)));

    /// <summary>已完成探测时直接返回结果，不启动探测。</summary>
    public bool TryGetCached(ToolId tool, out ToolInfo? info)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(tool, out var probe) && probe.IsCompletedSuccessfully)
            {
                info = probe.Result;
                return true;
            }
        }

        info = null;
        return false;
    }

    /// <summary>丢弃缓存，下次 <see cref="GetAsync"/> 重新探测。</summary>
    /// <param name="tool">要失效的工具；null 表示全部</param>
    public void Invalidate(ToolId? tool = null)
    {
        lock (_lock)
        {
            if (tool is { } id)
            {
                _cache.Remove(id);
            }
            else
            {
                _cache.Clear();
            }
        }

        Invalidated?.Invoke(this, tool);
    }

    private async Task<ToolInfo> ProbeAsync(ToolId tool)
    {
        var definition = _manifest.Get(tool);
        var recommended = definition.PackagesFor(_options.RuntimeIdentifier).FirstOrDefault()?.Version;
        var explicitPath = explicitPathProvider?.Invoke(tool);
        explicitPath = string.IsNullOrWhiteSpace(explicitPath) ? null : explicitPath;

        string? path;
        var explicitInvalid = false;
        try
        {
            path = Locate(definition, explicitPath);
            if (path is null && explicitPath is not null)
            {
                explicitInvalid = true;
                path = Locate(definition, null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ToolInfo(tool, null, null, null, ToolCapabilities.None, recommended, explicitPath is not null, ex.Message);
        }

        if (path is null)
        {
            return new ToolInfo(tool, null, null, null, ToolCapabilities.None, recommended, explicitInvalid, null);
        }

        string? versionText = null;
        var capabilities = ToolCapabilities.None;
        string? error = null;
        try
        {
            var version = await RunAsync(path, definition.VersionArguments);
            versionText = ToolOutputParser.ParseVersionText(tool, version.StandardOutput);
            if (tool == ToolId.Ffmpeg)
            {
                capabilities = await ProbeFfmpegAsync(path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
        }

        return new ToolInfo(tool, path, versionText, ToolOutputParser.ParseVersion(versionText), capabilities, recommended, explicitInvalid, error);
    }

    private async Task<ToolCapabilities> ProbeFfmpegAsync(string path)
    {
        var filtersTask = RunAsync(path, ["-hide_banner", "-filters"]);
        var encodersTask = RunAsync(path, ["-hide_banner", "-encoders"]);
        var filters = ToolOutputParser.ParseFilterNames((await filtersTask).StandardOutput);
        var encoders = ToolOutputParser.ParseEncoderNames((await encodersTask).StandardOutput);

        var capabilities = ToolCapabilities.None;
        if (filters.Contains("zscale"))
        {
            capabilities |= ToolCapabilities.Zscale;
        }

        if (filters.Contains("tonemap"))
        {
            capabilities |= ToolCapabilities.Tonemap;
        }

        if (encoders.Contains("libx264"))
        {
            capabilities |= ToolCapabilities.Libx264;
        }

        if (encoders.Contains("libx265"))
        {
            capabilities |= ToolCapabilities.Libx265;
            // 8-bit 版 x265 也会注册 libx265 编码器，只有像素格式列表能区分是否支持 10-bit
            var help = await RunAsync(path, ["-hide_banner", "-h", "encoder=libx265"]);
            if (ToolOutputParser.ParsePixelFormats(help.StandardOutput).Contains("yuv420p10le"))
            {
                capabilities |= ToolCapabilities.Libx265TenBit;
            }
        }

        return capabilities;
    }

    private Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> arguments) =>
        _options.Runner is { } runner
            ? runner(path, arguments, CancellationToken.None)
            : ProcessRunner.RunAsync(path, arguments, CancellationToken.None, _options.ProbeTimeout);

    private string? Locate(ToolDefinition definition, string? explicitPath)
    {
        if (_options.Locator is { } locator)
        {
            return locator(definition, explicitPath);
        }

        return explicitPath is not null
            ? ToolLocator.Find(definition.ExecutableFileName, explicitPath)
            : ToolLocator.Find(definition.ExecutableFileName, null, LegacySubdirectories(definition.Id));
    }

    /// <summary>手动解压时常见的子目录布局，与各转换器自身的查找保持一致。</summary>
    private static string[] LegacySubdirectories(ToolId tool) => tool switch
    {
        ToolId.ExifTool => ["ExifTool", "exiftool"],
        ToolId.Ffmpeg => ["ffmpeg", "FFmpeg", "bin"],
        _ => ["heif-enc", "libheif", "bin"]
    };
}
