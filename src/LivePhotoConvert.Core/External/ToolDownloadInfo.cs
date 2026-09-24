using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部工具的下载源
/// </summary>
/// <param name="Name">源名称（清单中的本地化资源键）</param>
/// <param name="Url">下载链接</param>
/// <param name="IsGitHubRelease">是否为 GitHub Release 链接（可接受自定义镜像前缀）</param>
public sealed record ToolDownloadSource(string Name, string Url, bool IsGitHubRelease = false);

/// <summary>
/// 下载进度报告
/// </summary>
/// <param name="SourceName">下载源名称</param>
/// <param name="DownloadedBytes">已下载字节数</param>
/// <param name="TotalBytes">文件总字节数，未知时为 null</param>
/// <param name="SpeedBytesPerSecond">即时下载速度 (字节/秒)</param>
public sealed record DownloadProgressReport(string SourceName, long DownloadedBytes, long? TotalBytes, double SpeedBytesPerSecond);

/// <summary>
/// 外部工具的下载与提取元数据（旧接口，下载源取自内嵌清单）。
/// </summary>
/// <param name="Id">清单中的工具标识</param>
/// <param name="ToolName">工具名称（如 "ExifTool", "FFmpeg"）</param>
/// <param name="TargetExecutableName">最终生成的可执行文件名（如 "exiftool.exe", "ffmpeg.exe"）</param>
/// <param name="Sources">按优先级排序的下载源列表</param>
/// <param name="ManualDownloadHelpUrl">全部下载失败时供用户手动下载的参考网址</param>
public sealed record ToolDownloadInfo(ToolId Id, string ToolName, string TargetExecutableName, IReadOnlyList<ToolDownloadSource> Sources, string ManualDownloadHelpUrl)
{
    /// <summary>从内嵌清单生成 Windows x64 的旧式元数据。</summary>
    internal static ToolDownloadInfo FromManifest(ToolId id)
    {
        var definition = ToolManifest.Embedded.Get(id);
        return new ToolDownloadInfo(
            id,
            definition.DisplayName,
            definition.ExecutableFileName,
            [.. definition.PackagesFor("win-x64").Select(package => new ToolDownloadSource(package.NameKey, package.Url, package.GithubRelease))],
            definition.Homepage);
    }
}

/// <summary>
/// 内置外部工具的下载定义（Windows x64）
/// </summary>
public static class ExternalToolMetadata
{
    public static readonly ToolDownloadInfo ExifTool = ToolDownloadInfo.FromManifest(ToolId.ExifTool);

    public static readonly ToolDownloadInfo FFmpeg = ToolDownloadInfo.FromManifest(ToolId.Ffmpeg);

    public static readonly ToolDownloadInfo HeifEnc = ToolDownloadInfo.FromManifest(ToolId.HeifEnc);
}
