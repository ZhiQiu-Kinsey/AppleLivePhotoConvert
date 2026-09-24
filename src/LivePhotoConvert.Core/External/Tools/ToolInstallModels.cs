namespace LivePhotoConvert.Core.External.Tools;

/// <summary>安装阶段。</summary>
public enum ToolInstallStage
{
    Downloading,
    Verifying,
    Extracting,
    Probing,
    Committing,

    /// <summary>当前下载源失败，即将尝试下一个源。</summary>
    SourceFailed
}

/// <summary>单个下载源失败的原因分类，供界面给出针对性提示。</summary>
public enum ToolFailureKind
{
    /// <summary>清单未提供可信哈希，未尝试下载。</summary>
    Unverified,
    Network,
    Stalled,
    IntegrityMismatch,
    UnsafeArchive,
    InvalidArchive,
    ProbeFailed,

    /// <summary>无法替换安装目录（通常是旧版本正在运行）。</summary>
    CommitFailed
}

/// <summary>一次下载尝试：清单中的包加上实际使用的地址（可能套了加速前缀）。</summary>
/// <param name="Package">清单中的下载包</param>
/// <param name="Url">实际请求地址</param>
/// <param name="MirrorPrefix">使用的加速前缀；直连时为 null</param>
public sealed record ToolDownloadCandidate(ToolPackage Package, Uri Url, string? MirrorPrefix);

/// <param name="PackageId">下载包标识</param>
/// <param name="NameKey">下载源名称的本地化资源键</param>
/// <param name="Url">实际请求地址</param>
/// <param name="Kind">失败分类</param>
/// <param name="Message">详细原因</param>
public sealed record ToolSourceFailure(string PackageId, string NameKey, Uri Url, ToolFailureKind Kind, string Message);

/// <summary>安装进度。</summary>
/// <param name="Tool">工具</param>
/// <param name="Stage">当前阶段</param>
/// <param name="Candidate">当前下载尝试</param>
/// <param name="Attempt">第几次尝试（从 1 开始）</param>
/// <param name="AttemptCount">尝试总数</param>
/// <param name="DownloadedBytes">已下载字节数</param>
/// <param name="TotalBytes">总字节数，服务器未告知时为 null</param>
/// <param name="BytesPerSecond">即时下载速度</param>
/// <param name="Failure">阶段为 <see cref="ToolInstallStage.SourceFailed"/> 时的失败原因</param>
public sealed record ToolInstallProgress(
    ToolId Tool,
    ToolInstallStage Stage,
    ToolDownloadCandidate Candidate,
    int Attempt,
    int AttemptCount,
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    ToolSourceFailure? Failure = null)
{
    /// <summary>本次尝试的整体完成度（0~1）：下载占 80%，其余阶段平分剩余部分；总大小未知时为 null。</summary>
    public double? Fraction => Stage switch
    {
        ToolInstallStage.Downloading => TotalBytes is > 0 ? 0.8 * Math.Clamp((double)DownloadedBytes / TotalBytes.Value, 0, 1) : null,
        ToolInstallStage.Verifying => 0.8,
        ToolInstallStage.Extracting => 0.85,
        ToolInstallStage.Probing => 0.95,
        ToolInstallStage.Committing => 0.98,
        _ => null
    };
}

/// <summary>安装成功的结果。</summary>
/// <param name="Tool">工具</param>
/// <param name="ExecutablePath">可执行文件完整路径</param>
/// <param name="InstallDirectory">安装目录</param>
/// <param name="Version">工具报告的版本文本</param>
/// <param name="PackageId">实际安装的下载包</param>
public sealed record ToolInstallResult(ToolId Tool, string ExecutablePath, string InstallDirectory, string? Version, string PackageId);

/// <summary>所有下载源都失败，或当前平台没有可用的下载包。</summary>
public sealed class ToolInstallException(ToolId tool, IReadOnlyList<ToolSourceFailure> failures, string message) : Exception(message)
{
    public ToolId Tool { get; } = tool;

    public IReadOnlyList<ToolSourceFailure> Failures { get; } = failures;

    /// <summary>清单中没有适用于当前平台的下载包。</summary>
    public bool IsPlatformUnsupported => Failures.Count == 0;
}
