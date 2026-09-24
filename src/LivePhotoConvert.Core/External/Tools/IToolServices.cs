namespace LivePhotoConvert.Core.External.Tools;

/// <summary><see cref="ToolRegistry"/> 的抽象，界面层测试可替换为不启动进程的实现。</summary>
public interface IToolRegistry
{
    /// <summary>缓存失效时触发；参数为失效的工具，null 表示全部。</summary>
    event EventHandler<ToolId?>? Invalidated;

    Task<ToolInfo> GetAsync(ToolId tool, CancellationToken cancellationToken = default);

    /// <summary>丢弃缓存，下次 <see cref="GetAsync"/> 重新探测。</summary>
    void Invalidate(ToolId? tool = null);
}

/// <summary><see cref="ToolInstaller"/> 的抽象，界面层测试可替换为不联网的实现。</summary>
public interface IToolInstaller
{
    ToolManifest Manifest { get; }

    /// <summary>安装根目录。</summary>
    string InstallRoot { get; }

    /// <inheritdoc cref="ToolInstaller.GetCandidates"/>
    IReadOnlyList<ToolDownloadCandidate> GetCandidates(ToolId tool, string? mirrorPrefix = null);

    /// <inheritdoc cref="ToolInstaller.InstallAsync"/>
    Task<ToolInstallResult> InstallAsync(ToolId tool, string? mirrorPrefix = null, IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ToolInstaller.RecoverInterruptedInstalls"/>
    void RecoverInterruptedInstalls();
}
