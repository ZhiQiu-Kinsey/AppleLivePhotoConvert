using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>安装器参数。</summary>
public sealed class ToolInstallerOptions
{
    /// <summary>连续多久收不到数据即判定当前源卡死并换下一个源。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>安装后试运行的超时。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>选择下载包所用的运行时标识。</summary>
    public string RuntimeIdentifier { get; init; } = ToolRuntime.CurrentRid;

    /// <summary>替换试运行逻辑：返回版本文本，无法运行时抛异常。</summary>
    internal Func<ToolDefinition, string, CancellationToken, Task<string?>>? Probe { get; init; }

    /// <summary>替换目录移动操作，用于模拟替换失败。</summary>
    internal Action<string, string>? MoveDirectory { get; init; }
}

/// <summary>
/// 按清单下载、校验、解压、试运行并原子替换外部工具。
/// </summary>
/// <remarks>
/// 所有中间产物都放在安装根目录下的 <c>.staging-*</c> 目录（与最终目录同卷，保证改名是原子的），
/// 任何失败或取消都会整体删除；旧版本在新版本就位前只被改名为 <c>.backup-*</c>，替换失败即改回。
/// </remarks>
public sealed class ToolInstaller : IToolInstaller
{
    private const string StagingPrefix = ".staging-";
    private const string BackupPrefix = ".backup-";
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    private readonly ToolManifest _manifest;
    private readonly ToolInstallerOptions _options;
    private readonly ToolPackageDownloader _downloader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="manifest">工具清单</param>
    /// <param name="installRoot">安装根目录，每个工具安装到其下的独立子目录</param>
    /// <param name="httpClient">下载用客户端；为 null 时使用不自动解压、无总超时的默认客户端</param>
    /// <param name="options">超时等参数</param>
    public ToolInstaller(ToolManifest manifest, string installRoot, HttpClient? httpClient = null, ToolInstallerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        _manifest = manifest;
        _options = options ?? new ToolInstallerOptions();
        InstallRoot = Path.GetFullPath(installRoot);
        _downloader = new ToolPackageDownloader(httpClient ?? SharedClient.Value, _options.IdleTimeout);
    }

    private static readonly Lazy<HttpClient> SharedClient = new(ToolPackageDownloader.CreateDefaultClient);

    /// <summary>使用内嵌清单与可写工具目录创建安装器。</summary>
    public static ToolInstaller CreateDefault() => new(ToolManifest.Embedded, ToolDirectories.GetWritableToolDirectory());

    public string InstallRoot { get; }

    public ToolManifest Manifest => _manifest;

    public string GetInstallDirectory(ToolId tool) => Path.Combine(InstallRoot, _manifest.Get(tool).InstallDirectory);

    public string GetExecutablePath(ToolId tool)
    {
        var definition = _manifest.Get(tool);
        return Path.Combine(InstallRoot, definition.InstallDirectory, definition.ExecutableFileName);
    }

    /// <summary>
    /// 按尝试顺序列出下载地址：每个包先走用户加速前缀，再直连，最后内置加速前缀。
    /// </summary>
    public IReadOnlyList<ToolDownloadCandidate> GetCandidates(ToolId tool, string? mirrorPrefix = null)
    {
        var userMirror = ToolDownloadUrls.NormalizeMirrorPrefix(mirrorPrefix);
        var candidates = new List<ToolDownloadCandidate>();
        foreach (var package in _manifest.Get(tool).PackagesFor(_options.RuntimeIdentifier))
        {
            if (!package.GithubRelease)
            {
                candidates.Add(new(package, new Uri(package.Url), null));
                continue;
            }

            if (userMirror is not null)
            {
                candidates.Add(new(package, new Uri(ToolDownloadUrls.ApplyMirror(package.Url, userMirror)), userMirror));
            }

            candidates.Add(new(package, new Uri(ToolDownloadUrls.ApplyMirror(package.Url, null)), null));
            foreach (var builtIn in _manifest.GithubMirrors.Select(ToolDownloadUrls.NormalizeMirrorPrefix).OfType<string>())
            {
                if (!builtIn.Equals(userMirror, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(new(package, new Uri(ToolDownloadUrls.ApplyMirror(package.Url, builtIn)), builtIn));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 依次尝试各下载源，直到安装成功。
    /// </summary>
    /// <param name="tool">要安装的工具</param>
    /// <param name="mirrorPrefix">用户配置的 GitHub 加速前缀</param>
    /// <param name="progress">进度回报</param>
    /// <param name="cancellationToken">取消后删除全部中间产物并保留旧版本</param>
    /// <exception cref="ToolInstallException">所有源都失败，或当前平台没有下载包</exception>
    /// <exception cref="OperationCanceledException">已取消</exception>
    public async Task<ToolInstallResult> InstallAsync(ToolId tool, string? mirrorPrefix = null, IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var definition = _manifest.Get(tool);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(InstallRoot);
            RecoverInterrupted(definition);

            var candidates = GetCandidates(tool, mirrorPrefix);
            if (candidates.Count == 0)
            {
                throw new ToolInstallException(tool, [], $"{definition.DisplayName} 没有适用于 {_options.RuntimeIdentifier} 的下载包，请参考 {definition.Homepage} 手动安装。");
            }

            var failures = new List<ToolSourceFailure>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var attempt = new Attempt(tool, candidate, index + 1, candidates.Count, progress);
                try
                {
                    return await InstallFromAsync(definition, attempt, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                {
                    var failure = new ToolSourceFailure(candidate.Package.Id, candidate.Package.NameKey, candidate.Url, Classify(ex, attempt.Stage), ex.Message);
                    failures.Add(failure);
                    attempt.Report(ToolInstallStage.SourceFailed, failure: failure);
                }
            }

            var details = string.Join("; ", failures.Select(f => $"[{f.PackageId} {f.Url.Host}] {f.Message}"));
            throw new ToolInstallException(tool, failures, $"{definition.DisplayName} 所有下载源均失败：{details}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ToolInstallResult> InstallFromAsync(ToolDefinition definition, Attempt attempt, CancellationToken cancellationToken)
    {
        var package = attempt.Candidate.Package;
        if (!package.IsVerified)
        {
            attempt.Stage = ToolInstallStage.Verifying;
            throw new ToolIntegrityException($"下载包 {package.Id} 没有可信的 SHA256，拒绝安装。");
        }

        var staging = Path.Combine(InstallRoot, $"{StagingPrefix}{definition.InstallDirectory}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var archivePath = Path.Combine(staging, "package.download");
            var payload = Path.Combine(staging, "payload");

            attempt.Stage = ToolInstallStage.Downloading;
            await _downloader.DownloadAsync(
                attempt.Candidate.Url,
                package,
                archivePath,
                (received, total, speed) => attempt.Report(ToolInstallStage.Downloading, received, total, speed),
                cancellationToken);
            attempt.Report(ToolInstallStage.Verifying);

            attempt.Stage = ToolInstallStage.Extracting;
            attempt.Report(ToolInstallStage.Extracting);
            var extracted = await ToolArchiveExtractor.ExtractAsync(package.Format, archivePath, payload, package.Root, package.Include, cancellationToken);
            FileHelper.TryDeleteFile(archivePath);
            if (extracted == 0)
            {
                throw new InvalidDataException($"下载包中没有 {package.Root} 下的文件。");
            }

            var executable = PrepareExecutable(definition, package, payload);

            attempt.Stage = ToolInstallStage.Probing;
            attempt.Report(ToolInstallStage.Probing);
            var version = await (_options.Probe ?? DefaultProbeAsync)(definition, executable, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            attempt.Stage = ToolInstallStage.Committing;
            attempt.Report(ToolInstallStage.Committing);
            var installDirectory = Path.Combine(InstallRoot, definition.InstallDirectory);
            Commit(definition, payload, installDirectory);
            return new ToolInstallResult(definition.Id, Path.Combine(installDirectory, definition.ExecutableFileName), installDirectory, version, package.Id);
        }
        finally
        {
            FileHelper.TryDeleteDirectory(staging);
        }
    }

    /// <summary>主程序统一改名为工具的可执行文件名（如 exiftool(-k).exe → exiftool.exe），并在类 Unix 系统上补可执行位。</summary>
    private static string PrepareExecutable(ToolDefinition definition, ToolPackage package, string payload)
    {
        var entryPath = Path.Combine(payload, package.Entry);
        if (!File.Exists(entryPath))
        {
            throw new InvalidDataException($"下载包中缺少主程序 {package.Root}/{package.Entry}。");
        }

        var executable = Path.Combine(payload, definition.ExecutableFileName);
        if (!string.Equals(package.Entry, definition.ExecutableFileName, StringComparison.Ordinal))
        {
            if (File.Exists(executable) && !string.Equals(package.Entry, definition.ExecutableFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"下载包中同时存在 {package.Entry} 与 {definition.ExecutableFileName}。");
            }

            File.Move(entryPath, executable);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return executable;
    }

    private async Task<string?> DefaultProbeAsync(ToolDefinition definition, string executable, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(executable, definition.VersionArguments, cancellationToken, _options.ProbeTimeout);
        if (!result.Success)
        {
            throw new InvalidOperationException($"{definition.ExecutableFileName} 试运行失败（退出码 {result.ExitCode}）：{result.ErrorSummary}");
        }

        return ToolOutputParser.ParseVersionText(definition.Id, result.StandardOutput);
    }

    /// <summary>
    /// 旧目录改名备份 → 新目录就位 → 删除备份；新目录就位失败时改回旧目录。
    /// </summary>
    private void Commit(ToolDefinition definition, string payload, string installDirectory)
    {
        string? backup = null;
        if (Directory.Exists(installDirectory))
        {
            backup = Path.Combine(InstallRoot, $"{BackupPrefix}{definition.InstallDirectory}-{Guid.NewGuid():N}");
            MoveDirectory(installDirectory, backup);
        }

        try
        {
            MoveDirectory(payload, installDirectory);
        }
        catch
        {
            if (backup is not null)
            {
                try
                {
                    MoveDirectory(backup, installDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 改回失败时备份仍在，下次安装前由 RecoverInterrupted 恢复；保留原始异常
                }
            }

            throw;
        }

        FileHelper.TryDeleteDirectory(backup);
    }

    /// <summary>
    /// 处理上次进程在替换中途退出留下的目录（安装前也会自动执行），适合在启动时调用一次。
    /// </summary>
    public void RecoverInterruptedInstalls()
    {
        if (!Directory.Exists(InstallRoot))
        {
            return;
        }

        _gate.Wait();
        try
        {
            foreach (var definition in _manifest.Tools)
            {
                RecoverInterrupted(definition);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 安装目录缺失时从备份恢复，其余备份与过期暂存删除。
    /// </summary>
    private void RecoverInterrupted(ToolDefinition definition)
    {
        var installDirectory = Path.Combine(InstallRoot, definition.InstallDirectory);
        var backups = Directory.EnumerateDirectories(InstallRoot, $"{BackupPrefix}{definition.InstallDirectory}-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToList();
        if (!Directory.Exists(installDirectory) && backups.Count > 0)
        {
            try
            {
                MoveDirectory(backups[0], installDirectory);
                backups.RemoveAt(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 恢复失败时保留备份，下次再试
                return;
            }
        }

        foreach (var backup in backups)
        {
            FileHelper.TryDeleteDirectory(backup);
        }

        var staleBefore = DateTime.UtcNow - StaleStagingAge;
        foreach (var staging in Directory.EnumerateDirectories(InstallRoot, $"{StagingPrefix}{definition.InstallDirectory}-*"))
        {
            if (Directory.GetLastWriteTimeUtc(staging) < staleBefore)
            {
                FileHelper.TryDeleteDirectory(staging);
            }
        }
    }

    /// <summary>刚写出的可执行文件可能正被杀毒软件扫描，短暂重试以免把瞬时占用当成失败。</summary>
    private void MoveDirectory(string source, string destination)
    {
        if (_options.MoveDirectory is { } move)
        {
            move(source, destination);
            return;
        }

        const int Attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private static ToolFailureKind Classify(Exception exception, ToolInstallStage stage) => exception switch
    {
        ToolIntegrityException when stage == ToolInstallStage.Verifying => ToolFailureKind.Unverified,
        ToolIntegrityException => ToolFailureKind.IntegrityMismatch,
        ToolDownloadStalledException => ToolFailureKind.Stalled,
        UnsafeArchiveException => ToolFailureKind.UnsafeArchive,
        InvalidDataException => ToolFailureKind.InvalidArchive,
        _ => stage switch
        {
            ToolInstallStage.Extracting => ToolFailureKind.InvalidArchive,
            ToolInstallStage.Probing => ToolFailureKind.ProbeFailed,
            ToolInstallStage.Committing => ToolFailureKind.CommitFailed,
            _ => ToolFailureKind.Network
        }
    };

    /// <summary>一次下载尝试的上下文，负责记录阶段与回报进度。</summary>
    private sealed class Attempt(ToolId tool, ToolDownloadCandidate candidate, int index, int count, IProgress<ToolInstallProgress>? progress)
    {
        public ToolDownloadCandidate Candidate { get; } = candidate;

        public ToolInstallStage Stage { get; set; } = ToolInstallStage.Downloading;

        private long _downloaded;
        private long? _total;

        public void Report(ToolInstallStage stage, long? downloaded = null, long? total = null, double speed = 0, ToolSourceFailure? failure = null)
        {
            if (downloaded is { } value)
            {
                _downloaded = value;
                _total = total;
            }

            progress?.Report(new ToolInstallProgress(tool, stage, Candidate, index, count, _downloaded, _total, speed, failure));
        }
    }
}
