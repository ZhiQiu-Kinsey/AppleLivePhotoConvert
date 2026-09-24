namespace LivePhotoConvert.Desktop.Features.Updates;

/// <summary>
/// 检查、下载并安装新版本。失败一律以 <see cref="UpdateException"/> 抛出（取消除外），调用方据 <see cref="UpdateException.Kind"/> 显示原因。
/// </summary>
public interface IUpdateService
{
    /// <summary>安装版与可自动更新的便携版为 true；源码运行、非 Windows 平台与旧版解压包为 false。</summary>
    bool IsSupported { get; }

    /// <summary>当前运行的版本号（不含构建元数据）。</summary>
    string CurrentVersion { get; }

    /// <summary>查询更新源；没有更新的正式版时返回 null。</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>下载并校验 <paramref name="update"/>（须来自最近一次 <see cref="CheckAsync"/>）。</summary>
    /// <param name="progress">0~100</param>
    Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>启动更新程序：等待本进程退出后安装并重新启动。调用方随后应尽快退出。</summary>
    void ApplyAndRestart();

    /// <summary>标记为退出时静默安装（不重新启动）；由 <see cref="OnExiting"/> 执行。</summary>
    void ApplyOnExit();

    /// <summary>程序退出收尾：已标记退出时安装且下载完成时启动更新程序。</summary>
    void OnExiting();
}

/// <summary>一次检查得到的新版本。</summary>
/// <param name="Version">新版本号</param>
/// <param name="NotesMarkdown">更新说明（Markdown），跨多个版本时按版本从新到旧合并</param>
/// <param name="DownloadBytes">需要下载的字节数（增量时为各增量包之和）</param>
/// <param name="IsDelta">是否增量更新</param>
public sealed record AvailableUpdate(string Version, string NotesMarkdown, long DownloadBytes, bool IsDelta);

/// <summary>更新失败的原因分类，界面据此显示本地化文案。</summary>
public enum UpdateFailureKind
{
    /// <summary>网络不通、超时或服务器返回错误。</summary>
    Network,

    /// <summary>GitHub 接口限流。</summary>
    RateLimited,

    /// <summary>下载内容与发布时记录的哈希不符。</summary>
    Integrity,

    /// <summary>磁盘空间不足或无法写入。</summary>
    Disk,

    /// <summary>发布中缺少更新清单或更新包。</summary>
    NotFound,

    /// <summary>其它错误。</summary>
    Unknown
}

public sealed class UpdateException(UpdateFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateFailureKind Kind { get; } = kind;
}
