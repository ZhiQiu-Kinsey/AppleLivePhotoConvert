using System.Text;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Models;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;

namespace LivePhotoConvert.Desktop.Features.Updates;

/// <summary>
/// 基于 Velopack 的更新：安装版与可更新的便携版由 Velopack 管理程序目录，检查与下载走 <see cref="GithubReleaseSource"/>。
/// </summary>
/// <remarks>
/// 只有 Program.Main 已执行 <c>VelopackApp.Build().Run()</c> 且当前程序确实由 Velopack 安装时才可用；
/// 其它运行方式（源码运行、旧版解压包、非 Windows）一律报告不支持，不访问网络。
/// </remarks>
public sealed class VelopackUpdateService : IUpdateService
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => GithubReleaseSource.CreateDefaultClient(AboutInfo.AppVersion));

    private readonly UpdateManager? _manager;
    private readonly GithubReleaseSource? _source;
    private readonly Lock _gate = new();
    private UpdateInfo? _pending;
    private string? _pendingVersion;
    private bool _applyOnExit;
    private bool _updaterLaunched;

    /// <param name="mirrorPrefix">读取用户设置的 GitHub 加速前缀</param>
    /// <param name="locator">为 null 时使用 Program.Main 中初始化的 Velopack 定位器；未初始化即不支持更新</param>
    /// <param name="http">为 null 时使用共享客户端</param>
    public VelopackUpdateService(Func<string?> mirrorPrefix, IVelopackLocator? locator = null, HttpClient? http = null)
    {
        locator ??= VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;
        if (locator?.CurrentlyInstalledVersion is not { } installed)
        {
            CurrentVersion = AboutInfo.AppVersion;
            return;
        }

        CurrentVersion = installed.ToNormalizedString();
        _source = new GithubReleaseSource(AboutInfo.RepositoryUrl, http ?? SharedClient.Value, mirrorPrefix, () => installed);
        _manager = new UpdateManager(_source, null, locator);
    }

    public bool IsSupported => _manager is not null;

    public string CurrentVersion { get; }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manager = _manager ?? throw new UpdateException(UpdateFailureKind.Unknown, "当前运行方式不支持自动更新。");
        UpdateInfo? info;
        try
        {
            info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Wrap(ex);
        }

        lock (_gate)
        {
            _pending = info;
            _pendingVersion = info?.TargetFullRelease.Version.ToNormalizedString();
        }

        if (info is null)
        {
            return null;
        }

        var target = info.TargetFullRelease;
        var deltas = info.DeltasToTarget;
        var deltaBytes = deltas.Sum(d => d.Size);
        // 与 UpdateManager 的判断一致：有本地基准包、增量链完整且不比完整包大时才走增量
        var isDelta = info.BaseRelease is not null && deltas.Length > 0 && deltas.Length <= 10 && deltaBytes <= target.Size;
        return new AvailableUpdate(
            target.Version.ToNormalizedString(),
            ComposeNotes(_source!.NewerReleases, target.NotesMarkdown),
            isDelta ? deltaBytes : target.Size,
            isDelta);
    }

    public async Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        var manager = _manager ?? throw new UpdateException(UpdateFailureKind.Unknown, "当前运行方式不支持自动更新。");
        UpdateInfo info;
        lock (_gate)
        {
            if (_pending is null || _pendingVersion != update.Version)
            {
                throw new InvalidOperationException("只能下载最近一次检查得到的版本。");
            }

            info = _pending;
        }

        try
        {
            await manager.DownloadUpdatesAsync(info, p => progress?.Report(p), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Wrap(ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void ApplyAndRestart()
    {
        var manager = _manager ?? throw new InvalidOperationException("当前运行方式不支持自动更新。");
        var asset = manager.UpdatePendingRestart ?? throw new InvalidOperationException("没有已下载的更新。");
        lock (_gate)
        {
            manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true);
            // 启动成功后才标记：更新程序没能启动时，退出收尾仍要尝试"退出时安装"
            _updaterLaunched = true;
        }
    }

    public void ApplyOnExit()
    {
        lock (_gate)
        {
            _applyOnExit = true;
        }
    }

    public void OnExiting()
    {
        lock (_gate)
        {
            if (!_applyOnExit || _updaterLaunched || _manager?.UpdatePendingRestart is not { } asset)
            {
                return;
            }

            _updaterLaunched = true;
            try
            {
                // 静默安装、不重启；更新程序等待本进程退出后才替换文件
                _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
            }
            catch (Exception ex)
            {
                // 下次启动时 VelopackApp 仍会自动安装已下载的更新
                ErrorLogger.Log(ex, "退出时启动更新程序");
            }
        }
    }

    /// <summary>跨多个版本时按版本从新到旧合并说明，每段以版本号为标题；只有一个版本时不加标题。</summary>
    internal static string ComposeNotes(IReadOnlyList<ReleaseNotesEntry> releases, string? fallback)
    {
        var withNotes = releases.Where(r => !string.IsNullOrWhiteSpace(r.NotesMarkdown)).ToList();
        if (withNotes.Count == 0)
        {
            return fallback?.Trim() ?? string.Empty;
        }

        if (withNotes.Count == 1)
        {
            return withNotes[0].NotesMarkdown;
        }

        var builder = new StringBuilder();
        foreach (var release in withNotes)
        {
            builder.Append("## ").AppendLine(release.Version).AppendLine().AppendLine(release.NotesMarkdown).AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static Exception Wrap(Exception ex) => ex switch
    {
        UpdateException update => update,
        ChecksumFailedException => new UpdateException(UpdateFailureKind.Integrity, ex.Message, ex),
        HttpRequestException => new UpdateException(UpdateFailureKind.Network, ex.Message, ex),
        IOException or UnauthorizedAccessException => new UpdateException(UpdateFailureKind.Disk, ex.Message, ex),
        _ => new UpdateException(UpdateFailureKind.Unknown, ex.Message, ex)
    };
}
