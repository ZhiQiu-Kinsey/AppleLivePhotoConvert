using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 旧的下载入口，仅为尚未迁移的调用方保留；内部全部委托给 <see cref="ToolInstaller"/>（哈希校验、路径穿越防护、原子替换）。
/// </summary>
public static class ToolDownloader
{
    /// <inheritdoc cref="ToolDirectories.LocalAppDataToolDirectory"/>
    public static string LocalAppDataToolDirectory => ToolDirectories.LocalAppDataToolDirectory;

    /// <inheritdoc cref="ToolDirectories.GetWritableToolDirectory"/>
    public static string GetWritableToolDirectory() => ToolDirectories.GetWritableToolDirectory();

    /// <summary>
    /// 按内嵌清单下载、校验并安装工具。
    /// </summary>
    /// <param name="tool">工具下载元数据（只用于确定是哪个工具，下载源以清单为准）</param>
    /// <param name="customMirror">用户自定义 GitHub 加速前缀</param>
    /// <param name="progress">下载进度回调</param>
    /// <param name="onSourceSwitch">开始尝试某个源（异常为 null）或该源失败（附带原因）时的通知</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>安装后的可执行文件完整路径</returns>
    /// <exception cref="InvalidOperationException">所有下载源均失败</exception>
    public static async Task<string> DownloadAndExtractAsync(
        ToolDownloadInfo tool,
        string? customMirror = null,
        IProgress<DownloadProgressReport>? progress = null,
        Action<ToolDownloadSource, Exception?>? onSourceSwitch = null,
        CancellationToken cancellationToken = default)
    {
        var installer = ToolInstaller.CreateDefault();
        var lastAttempt = 0;
        var adapter = new SynchronousProgress<ToolInstallProgress>(report =>
        {
            var source = new ToolDownloadSource(report.Candidate.Package.NameKey, report.Candidate.Url.AbsoluteUri, report.Candidate.Package.GithubRelease);
            if (report.Stage == ToolInstallStage.SourceFailed)
            {
                onSourceSwitch?.Invoke(source, new InvalidOperationException(report.Failure?.Message));
                return;
            }

            if (report.Attempt != lastAttempt)
            {
                lastAttempt = report.Attempt;
                onSourceSwitch?.Invoke(source, null);
            }

            if (report.Stage == ToolInstallStage.Downloading)
            {
                progress?.Report(new DownloadProgressReport(source.Name, report.DownloadedBytes, report.TotalBytes, report.BytesPerSecond));
            }
        });

        try
        {
            var result = await installer.InstallAsync(tool.Id, customMirror, adapter, cancellationToken);
            return result.ExecutablePath;
        }
        catch (ToolInstallException ex)
        {
            throw new InvalidOperationException(
                $"{ex.Message}\n您可以手动下载该工具（参考 {tool.ManualDownloadHelpUrl}），并将 {tool.TargetExecutableName} 放入以下目录之一：\n"
                + $"  1. 程序目录: {AppContext.BaseDirectory}\n"
                + $"  2. 工具目录: {installer.InstallRoot}\n"
                + "  3. 或将其所在目录加入系统环境变量 PATH。",
                ex);
        }
    }

    /// <summary>在回报线程上直接调用，保证源切换通知与进度的先后顺序。</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
