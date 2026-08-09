namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部工具的下载源
/// </summary>
/// <param name="Name">源的名称（例如 "国内加速镜像 1"、"官方源"）</param>
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
/// 外部工具的下载与提取元数据
/// </summary>
/// <param name="ToolName">工具名称（如 "ExifTool", "FFmpeg"）</param>
/// <param name="TargetExecutableName">最终生成的可执行文件名（如 "exiftool.exe", "ffmpeg.exe"）</param>
/// <param name="Sources">按优先级排序的下载源列表</param>
/// <param name="ZipEntryFilter">从 Zip 压缩包中匹配目标可执行文件的筛选条件</param>
/// <param name="ManualDownloadHelpUrl">全部下载失败时供用户手动下载的参考网址</param>
public sealed record ToolDownloadInfo(string ToolName, string TargetExecutableName, IReadOnlyList<ToolDownloadSource> Sources, Func<string, bool> ZipEntryFilter, string ManualDownloadHelpUrl)
{
    /// <summary>
    /// 获取针对用户自定义镜像前缀调整后的下载源列表
    /// </summary>
    /// <param name="customMirrorPrefix">用户指定的 GitHub 镜像加速前缀（例如 "https://mirror.ghproxy.com/"）</param>
    /// <returns>调整后的下载源列表</returns>
    public IReadOnlyList<ToolDownloadSource> GetEffectiveSources(string? customMirrorPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(customMirrorPrefix))
        {
            return Sources;
        }

        var normalizedPrefix = customMirrorPrefix.Trim();
        if (!normalizedPrefix.EndsWith('/'))
        {
            normalizedPrefix += "/";
        }

        var list = new List<ToolDownloadSource>();

        // 将用户自定义的镜像作为最高优先级源加入
        foreach (var source in Sources)
        {
            if (source.IsGitHubRelease)
            {
                var acceleratedUrl = normalizedPrefix + source.Url;
                list.Add(new ToolDownloadSource($"自定义加速镜像 ({source.Name})", acceleratedUrl));
            }
        }

        list.AddRange(Sources);
        return list;
    }
}

/// <summary>
/// 内置外部工具的下载定义
/// </summary>
public static class ExternalToolMetadata
{
    /// <summary>
    /// ExifTool 的下载元数据（Windows）
    /// </summary>
    public static readonly ToolDownloadInfo ExifTool = new(
        ToolName: "ExifTool",
        TargetExecutableName: OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool",
        Sources:
        [
            new ToolDownloadSource("阿里云国内高速镜像 (npmmirror)", "https://registry.npmmirror.com/exiftool-vendored.exe/-/exiftool-vendored.exe-13.59.2.tgz"),
            new ToolDownloadSource("国内 GitHub 加速镜像 (ghfast.top)", "https://ghfast.top/https://github.com/exiftool/exiftool/archive/refs/tags/13.59.zip", IsGitHubRelease: true),
            new ToolDownloadSource("GitHub 官方源", "https://github.com/exiftool/exiftool/archive/refs/tags/13.59.zip", IsGitHubRelease: true)
        ],
        ZipEntryFilter: entry =>
        {
            var normalized = entry.Replace('\\', '/');
            return normalized.EndsWith("/exiftool(-k).exe", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("/exiftool.exe", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("/vendor/exiftool.exe", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "exiftool(-k).exe", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "exiftool.exe", StringComparison.OrdinalIgnoreCase)
                   || !OperatingSystem.IsWindows() && normalized.EndsWith("/exiftool", StringComparison.OrdinalIgnoreCase);
        },
        ManualDownloadHelpUrl: "https://exiftool.org/");

    /// <summary>
    /// FFmpeg 的下载元数据（Windows）
    /// </summary>
    public static readonly ToolDownloadInfo FFmpeg = new(
        ToolName: "FFmpeg",
        TargetExecutableName: OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
        Sources:
        [
            new ToolDownloadSource("阿里云国内高速镜像 (npmmirror)", "https://registry.npmmirror.com/@ffmpeg-binary/win32-x64/-/win32-x64-7.0.0.tgz"),
            new ToolDownloadSource("国内 GitHub 加速镜像 (ghfast.top)", "https://ghfast.top/https://github.com/GyanD/codexffmpeg/releases/download/7.0.2/ffmpeg-7.0.2-essentials_build.zip", IsGitHubRelease: true),
            new ToolDownloadSource("国内 GitHub BtbN 镜像 (ghfast.top)", "https://ghfast.top/https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", IsGitHubRelease: true),
            new ToolDownloadSource("GitHub 官方源", "https://github.com/GyanD/codexffmpeg/releases/download/7.0.2/ffmpeg-7.0.2-essentials_build.zip", IsGitHubRelease: true)
        ],
        ZipEntryFilter: entry =>
        {
            var normalized = entry.Replace('\\', '/');
            return normalized.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                   || !OperatingSystem.IsWindows() && (normalized.EndsWith("/bin/ffmpeg", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "ffmpeg", StringComparison.OrdinalIgnoreCase));
        },
        ManualDownloadHelpUrl: "https://ffmpeg.org/download.html");
}
