using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>受管理的外部工具。</summary>
public enum ToolId
{
    [JsonStringEnumMemberName("exiftool")]
    ExifTool,

    [JsonStringEnumMemberName("ffmpeg")]
    Ffmpeg,

    [JsonStringEnumMemberName("heif-enc")]
    HeifEnc
}

/// <summary>下载包的压缩格式。</summary>
public enum ToolArchiveFormat
{
    [JsonStringEnumMemberName("zip")]
    Zip,

    [JsonStringEnumMemberName("tgz")]
    Tgz,

    [JsonStringEnumMemberName("7z")]
    SevenZip
}

/// <summary>
/// 内嵌的外部工具清单：锁定版本、下载源与校验值。
/// </summary>
/// <param name="SchemaVersion">清单格式版本</param>
/// <param name="GithubMirrors">GitHub 源的内置加速前缀，排在直连之后作为兜底</param>
/// <param name="Tools">工具定义</param>
public sealed record ToolManifest(int SchemaVersion, IReadOnlyList<string> GithubMirrors, IReadOnlyList<ToolDefinition> Tools)
{
    public const int CurrentSchemaVersion = 1;

    private const string ResourceName = "LivePhotoConvert.Core.External.Tools.tools.json";

    /// <summary>程序内嵌的清单，首次访问时解析并校验。</summary>
    public static ToolManifest Embedded => EmbeddedHolder.Value;

    private static readonly Lazy<ToolManifest> EmbeddedHolder = new(LoadEmbedded);

    public ToolDefinition Get(ToolId id) =>
        Tools.FirstOrDefault(tool => tool.Id == id) ?? throw new KeyNotFoundException($"清单中没有工具 {id}。");

    /// <summary>从 JSON 解析并校验清单。</summary>
    /// <exception cref="InvalidDataException">JSON 无效或内容不满足约束</exception>
    public static ToolManifest Parse(Stream json)
    {
        ToolManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, ToolManifestJsonContext.Default.ToolManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"工具清单格式无效：{ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("工具清单为空。");
        }

        manifest.Validate();
        return manifest;
    }

    public static ToolManifest Parse(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Parse(stream);
    }

    private static ToolManifest LoadEmbedded()
    {
        using var stream = typeof(ToolManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少内嵌资源 {ResourceName}。");
        return Parse(stream);
    }

    /// <summary>
    /// 校验清单约束；违反任何一条都说明清单本身有错，应在测试阶段暴露而不是在用户机器上下载后才发现。
    /// </summary>
    private void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"不支持的工具清单版本 {SchemaVersion}。");
        }

        foreach (var mirror in GithubMirrors)
        {
            if (!ToolDownloadUrls.IsHttpUrl(mirror))
            {
                throw new InvalidDataException($"GitHub 加速前缀不是有效的 HTTP 地址：{mirror}");
            }
        }

        var seenTools = new HashSet<ToolId>();
        var seenPackages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in Tools)
        {
            if (!seenTools.Add(tool.Id))
            {
                throw new InvalidDataException($"工具 {tool.Id} 重复定义。");
            }

            RequireFileName(tool.Executable, $"{tool.Id}.executable");
            RequireFileName(tool.InstallDirectory, $"{tool.Id}.installDirectory");
            if (tool.Packages.Count == 0)
            {
                throw new InvalidDataException($"工具 {tool.Id} 没有下载包。");
            }

            foreach (var package in tool.Packages)
            {
                if (!seenPackages.Add(package.Id))
                {
                    throw new InvalidDataException($"下载包 {package.Id} 重复定义。");
                }

                package.Validate();
            }
        }
    }

    internal static void RequireFileName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\', ':']) >= 0
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"{field} 必须是单个文件名：{value}");
        }
    }
}

/// <param name="Id">工具标识</param>
/// <param name="DisplayName">界面显示名（产品名，不做本地化）</param>
/// <param name="Executable">可执行文件名（不含扩展名，Windows 上自动补 .exe）</param>
/// <param name="InstallDirectory">安装到工具目录下的子目录名</param>
/// <param name="VersionArguments">查询版本的命令行参数</param>
/// <param name="Homepage">手动下载参考页</param>
/// <param name="Packages">按优先级排列的下载包</param>
public sealed record ToolDefinition(
    ToolId Id,
    string DisplayName,
    string Executable,
    string InstallDirectory,
    IReadOnlyList<string> VersionArguments,
    string Homepage,
    IReadOnlyList<ToolPackage> Packages)
{
    public string ExecutableFileName => OperatingSystem.IsWindows() ? Executable + ".exe" : Executable;

    /// <summary>适用于指定运行时标识的下载包，保持清单顺序。</summary>
    public IEnumerable<ToolPackage> PackagesFor(string runtimeIdentifier) =>
        Packages.Where(package => string.Equals(package.Rid, runtimeIdentifier, StringComparison.OrdinalIgnoreCase));
}

/// <param name="Id">下载包唯一标识</param>
/// <param name="NameKey">下载源名称的本地化资源键</param>
/// <param name="Rid">适用的运行时标识，如 win-x64</param>
/// <param name="Version">包内工具的版本（与工具自身报告的版本一致）</param>
/// <param name="Url">下载地址</param>
/// <param name="Sha256">整包 SHA256（十六进制）；缺失的包视为未校验，安装时拒绝</param>
/// <param name="Integrity">npm 的 dist.integrity（sha512-Base64），存在时额外校验</param>
/// <param name="Format">压缩格式</param>
/// <param name="Root">包内作为安装目录内容的子目录（正斜杠分隔，空串表示包根）</param>
/// <param name="Entry">主程序相对 <paramref name="Root"/> 的文件名，安装后重命名为工具的可执行文件名</param>
/// <param name="Include">只解出这些相对 <paramref name="Root"/> 的文件或目录；为空时解出全部</param>
/// <param name="GithubRelease">是否为 GitHub Release 地址（可套用加速前缀）</param>
public sealed record ToolPackage(
    string Id,
    string NameKey,
    string Rid,
    string Version,
    string Url,
    ToolArchiveFormat Format,
    string Root,
    string Entry,
    string? Sha256 = null,
    string? Integrity = null,
    IReadOnlyList<string>? Include = null,
    bool GithubRelease = false)
{
    /// <summary>是否带有可用于校验的 SHA256。</summary>
    public bool IsVerified => Sha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(NameKey) || string.IsNullOrWhiteSpace(Rid) || string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidDataException($"下载包 {Id} 缺少必填字段。");
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"下载包 {Id} 的地址必须是 HTTPS：{Url}");
        }

        if (Sha256 is not null && !IsVerified)
        {
            throw new InvalidDataException($"下载包 {Id} 的 SHA256 格式无效。");
        }

        if (Integrity is not null && !ToolIntegrity.TryParseSha512(Integrity, out _))
        {
            throw new InvalidDataException($"下载包 {Id} 的 integrity 必须是 sha512-Base64。");
        }

        ToolManifest.RequireFileName(Entry, $"{Id}.entry");
        try
        {
            if (Root.Length > 0)
            {
                _ = ToolArchivePath.Normalize(Root);
            }

            foreach (var item in Include ?? [])
            {
                _ = ToolArchivePath.Normalize(item);
            }
        }
        catch (UnsafeArchiveException ex)
        {
            throw new InvalidDataException($"下载包 {Id} 的 root/include 无效：{ex.Message}", ex);
        }

        if (GithubRelease && !Url.StartsWith(ToolDownloadUrls.GithubPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"下载包 {Id} 标记为 GitHub Release，但地址不是 {ToolDownloadUrls.GithubPrefix}。");
        }
    }
}

/// <summary>当前进程的运行时标识（win-x64 / linux-x64 / osx-arm64 等）。</summary>
public static class ToolRuntime
{
    public static string CurrentRid { get; } = Compute();

    private static string Compute()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ToolManifest))]
internal sealed partial class ToolManifestJsonContext : JsonSerializerContext;
