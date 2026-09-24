using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

public class ToolInstallerTests
{
    private const string PrimaryUrl = "https://packages.example.com/tool-1.2.3.tgz";
    private const string BackupUrl = "https://backup.example.com/tool-1.2.3.tgz";
    private const string GithubUrl = "https://github.com/owner/repo/releases/download/v1/tool.zip";

    private static byte[] ToolTgz(string version = "1.2.3") => ToolPackages.Tgz(
        ToolPackages.TarFile($"package/{ToolPackages.ExecutableFileName}", version),
        ToolPackages.TarFile("package/lib/dep.dll", "dependency"),
        ToolPackages.TarFile("package/README.md", "readme"));

    [Fact]
    public async Task Install_VerifiesExtractsAndPlacesToolInItsDirectory()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content, integrity: ToolPackages.Integrity(content))),
            temp.Root, http.CreateClient(), ToolPackages.Options());
        var stages = new List<ToolInstallStage>();

        var result = await installer.InstallAsync(ToolId.ExifTool, progress: new InlineProgress(p => stages.Add(p.Stage)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(installer.GetExecutablePath(ToolId.ExifTool), result.ExecutablePath);
        Assert.Equal(Path.Combine(temp.Root, ToolPackages.InstallDirectory), result.InstallDirectory);
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal("primary", result.PackageId);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "lib", "dep.dll")));
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
        Assert.Equal(
            [ToolInstallStage.Downloading, ToolInstallStage.Verifying, ToolInstallStage.Extracting, ToolInstallStage.Probing, ToolInstallStage.Committing],
            stages.Distinct());
    }

    [Fact]
    public async Task Install_RenamesEntryToExecutableName()
    {
        using var temp = new TempDirectory();
        var content = ToolPackages.Zip(("tool-1.2.3_64/fake-tool(-k).exe", "1.2.3"), ("tool-1.2.3_64/fake-tool_files/perl.dll", "dll"));
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("zip", PrimaryUrl, content, ToolArchiveFormat.Zip, "tool-1.2.3_64", "fake-tool(-k).exe")),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var result = await installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolPackages.ExecutableFileName, Path.GetFileName(result.ExecutablePath));
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.False(File.Exists(Path.Combine(result.InstallDirectory, "fake-tool(-k).exe")));
    }

    [Fact]
    public async Task Install_HashMismatch_RejectsAndLeavesNothing()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content, sha256: new string('0', 64))),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ToolFailureKind.IntegrityMismatch, Assert.Single(error.Failures).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Root));
    }

    [Fact]
    public async Task Install_IntegrityMismatch_RejectsEvenWhenSha256Matches()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content, integrity: ToolPackages.Integrity([1, 2, 3]))),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ToolFailureKind.IntegrityMismatch, Assert.Single(error.Failures).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Root));
    }

    [Fact]
    public async Task Install_UnverifiedPackage_IsSkippedWithoutDownloading()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content).Serve(BackupUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(
                ToolPackages.Package("unverified", PrimaryUrl, null),
                ToolPackages.Package("verified", BackupUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        ToolSourceFailure? failure = null;
        var result = await installer.InstallAsync(ToolId.ExifTool, progress: new InlineProgress(p => failure ??= p.Failure), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("verified", result.PackageId);
        Assert.Equal(ToolFailureKind.Unverified, failure?.Kind);
        Assert.Equal([BackupUrl], http.Requests);
    }

    [Fact]
    public async Task Install_FailedSource_FallsBackToNext()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Fail(PrimaryUrl).Serve(BackupUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(
                ToolPackages.Package("primary", PrimaryUrl, content),
                ToolPackages.Package("backup", BackupUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var result = await installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("backup", result.PackageId);
        Assert.Equal([PrimaryUrl, BackupUrl], http.Requests);
    }

    [Fact]
    public async Task Install_StalledDownload_FailsWithIdleTimeoutAndLeavesNothing()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var http = new FakeHttpHandler().Stall(PrimaryUrl, content[..10]);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options(idleTimeout: TimeSpan.FromMilliseconds(300)));

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(ToolFailureKind.Stalled, Assert.Single(error.Failures).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Root));
    }

    [Fact]
    public async Task Install_Canceled_KeepsOldVersionAndLeavesNoStaging()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, ToolPackages.ExecutableFileName), "old"u8.ToArray());
        var content = ToolTgz();
        var http = new FakeHttpHandler().Stall(PrimaryUrl, content[..10]);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options(idleTimeout: TimeSpan.FromMinutes(5)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // 下载开始后稍等片刻再取消，让部分数据先写入暂存文件
        var progress = new InlineProgress(p =>
        {
            if (p.Stage == ToolInstallStage.Downloading)
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(ToolId.ExifTool, progress: progress, cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("old", File.ReadAllText(existing));
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_ReplacesExistingVersionAtomically()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, ToolPackages.ExecutableFileName), "1.0.0"u8.ToArray());
        temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, "stale.dll"), "stale"u8.ToArray());
        var content = ToolTgz("2.0.0");
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var result = await installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("2.0.0", File.ReadAllText(result.ExecutablePath));
        Assert.False(File.Exists(Path.Combine(result.InstallDirectory, "stale.dll")));
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_CommitFailure_RollsBackToOldVersion()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, ToolPackages.ExecutableFileName), "1.0.0"u8.ToArray());
        var content = ToolTgz("2.0.0");
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var moves = new List<(string Source, string Destination)>();
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(),
            ToolPackages.Options(moveDirectory: (source, destination) =>
            {
                moves.Add((source, destination));
                if (source.EndsWith("payload", StringComparison.Ordinal))
                {
                    throw new IOException("模拟新目录就位失败");
                }

                Directory.Move(source, destination);
            }));

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ToolFailureKind.CommitFailed, Assert.Single(error.Failures).Kind);
        Assert.Equal("1.0.0", File.ReadAllText(existing));
        Assert.Equal(3, moves.Count);
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_UnsafeArchive_KeepsOldVersion()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, ToolPackages.ExecutableFileName), "1.0.0"u8.ToArray());
        var content = ToolPackages.Tgz(
            ToolPackages.TarFile($"package/{ToolPackages.ExecutableFileName}", "2.0.0"),
            ToolPackages.TarFile($"package/../../{ToolPackages.InstallDirectory}/{ToolPackages.ExecutableFileName}", "evil"));
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(), ToolPackages.Options());

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ToolFailureKind.UnsafeArchive, Assert.Single(error.Failures).Kind);
        Assert.Equal("1.0.0", File.ReadAllText(existing));
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_ProbeFailure_KeepsOldVersion()
    {
        using var temp = new TempDirectory();
        var existing = temp.CreateFile(Path.Combine(ToolPackages.InstallDirectory, ToolPackages.ExecutableFileName), "1.0.0"u8.ToArray());
        var content = ToolTgz("2.0.0");
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient(),
            new ToolInstallerOptions { Probe = (_, _, _) => throw new InvalidOperationException("无法启动") });

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ToolFailureKind.ProbeFailed, Assert.Single(error.Failures).Kind);
        Assert.Equal("1.0.0", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Install_NoPackageForPlatform_Throws()
    {
        using var temp = new TempDirectory();
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, ToolTgz())),
            temp.Root, new FakeHttpHandler().CreateClient(),
            new ToolInstallerOptions { RuntimeIdentifier = "none-x64" });

        var error = await Assert.ThrowsAsync<ToolInstallException>(() => installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error.IsPlatformUnsupported);
    }

    [Fact]
    public void GetCandidates_OrdersUserMirrorDirectThenBuiltInMirrors()
    {
        using var temp = new TempDirectory();
        var installer = new ToolInstaller(
            ToolPackages.Manifest(
                ["https://builtin.example/", "https://user.example/"],
                ToolPackages.Package("npm", PrimaryUrl, [1]),
                ToolPackages.Package("github", GithubUrl, [1], ToolArchiveFormat.Zip, githubRelease: true)),
            temp.Root, new FakeHttpHandler().CreateClient());

        var urls = installer.GetCandidates(ToolId.ExifTool, "https://user.example").Select(c => c.Url.AbsoluteUri);

        Assert.Equal(
            [
                PrimaryUrl,
                "https://user.example/" + GithubUrl,
                GithubUrl,
                "https://builtin.example/" + GithubUrl
            ],
            urls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://github.com/")]
    public void GetCandidates_WithoutUsableMirror_StartsWithDirect(string? mirror)
    {
        using var temp = new TempDirectory();
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("github", GithubUrl, [1], ToolArchiveFormat.Zip, githubRelease: true)),
            temp.Root, new FakeHttpHandler().CreateClient());

        Assert.Equal([GithubUrl], installer.GetCandidates(ToolId.ExifTool, mirror).Select(c => c.Url.AbsoluteUri));
    }

    [Fact]
    public void ApplyMirror_StripsExistingProxy()
    {
        Assert.Equal(
            "https://ghfast.top/" + GithubUrl,
            ToolDownloadUrls.ApplyMirror("https://gh-proxy.com/" + GithubUrl, "https://ghfast.top"));
    }

    [Fact]
    public async Task Install_ThroughMirror_StillVerifiesManifestHash()
    {
        using var temp = new TempDirectory();
        var content = ToolTgz();
        var tampered = ToolTgz("6.6.6");
        var http = new FakeHttpHandler().Serve("https://mirror.example/" + GithubUrl, tampered).Serve(GithubUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("github", GithubUrl, content, githubRelease: true)),
            temp.Root, http.CreateClient(), ToolPackages.Options());
        var failures = new List<ToolSourceFailure>();

        var result = await installer.InstallAsync(ToolId.ExifTool, "https://mirror.example/", new InlineProgress(p =>
        {
            if (p.Failure is { } failure)
            {
                failures.Add(failure);
            }
        }), TestContext.Current.CancellationToken);

        Assert.Equal("1.2.3", result.Version);
        Assert.Equal(ToolFailureKind.IntegrityMismatch, Assert.Single(failures).Kind);
    }

    [Fact]
    public void RecoverInterruptedInstalls_RestoresBackupWhenInstallDirectoryIsMissing()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine($".backup-{ToolPackages.InstallDirectory}-{Guid.NewGuid():N}", ToolPackages.ExecutableFileName), "1.0.0"u8.ToArray());
        var installer = new ToolInstaller(ToolPackages.Manifest(ToolPackages.Package("p", PrimaryUrl, [1])), temp.Root, new FakeHttpHandler().CreateClient());

        installer.RecoverInterruptedInstalls();

        Assert.Equal("1.0.0", File.ReadAllText(installer.GetExecutablePath(ToolId.ExifTool)));
        Assert.Equal([ToolPackages.InstallDirectory], Directory.EnumerateFileSystemEntries(temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Install_DefaultProbe_RunsInstalledExecutable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "用 shell 脚本模拟工具，仅在类 Unix 系统上运行。");
        using var temp = new TempDirectory();
        var content = ToolPackages.Tgz(ToolPackages.TarFile($"package/{ToolPackages.ExecutableFileName}", "#!/bin/sh\necho 4.5.6\n"));
        var http = new FakeHttpHandler().Serve(PrimaryUrl, content);
        var installer = new ToolInstaller(
            ToolPackages.Manifest(ToolPackages.Package("primary", PrimaryUrl, content)),
            temp.Root, http.CreateClient());

        var result = await installer.InstallAsync(ToolId.ExifTool, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("4.5.6", result.Version);
    }

    /// <summary>同步回调，避免 <see cref="Progress{T}"/> 投递到线程池导致断言时序不确定。</summary>
    private sealed class InlineProgress(Action<ToolInstallProgress> handler) : IProgress<ToolInstallProgress>
    {
        public void Report(ToolInstallProgress value) => handler(value);
    }
}
