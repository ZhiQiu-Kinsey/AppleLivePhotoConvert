using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 用真实下载包验证清单的哈希、根目录、入口与筛选是否正确。
/// 包体积大（FFmpeg 近 200MB），不放进仓库：把包按清单中的包 id 命名放进
/// 环境变量 LPC_TOOL_PACKAGE_DIR 指向的目录后运行，缺失的包跳过。
/// </summary>
public class ToolManifestPackageTests
{
    public static TheoryData<string> PackageIds =>
        [.. ToolManifest.Embedded.Tools.SelectMany(tool => tool.Packages).Select(package => package.Id)];

    [Theory]
    [MemberData(nameof(PackageIds))]
    public async Task RealPackage_InstallsWithManifestSettings(string packageId)
    {
        var directory = Environment.GetEnvironmentVariable("LPC_TOOL_PACKAGE_DIR");
        var archive = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, packageId);
        Assert.SkipWhen(archive is null || !File.Exists(archive), $"未提供下载包 {packageId}。");

        var tool = ToolManifest.Embedded.Tools.Single(t => t.Packages.Any(p => p.Id == packageId));
        var package = tool.Packages.Single(p => p.Id == packageId);
        // 只保留这一个包，并按包的平台安装，Windows 可执行文件在其他系统上不做试运行
        var manifest = ToolManifest.Embedded with { GithubMirrors = [], Tools = [tool with { Packages = [package] }] };
        var http = new FakeHttpHandler().Serve(package.Url, await File.ReadAllBytesAsync(archive!, TestContext.Current.CancellationToken));
        using var temp = new TempDirectory();
        var installer = new ToolInstaller(manifest, temp.Root, http.CreateClient(), new ToolInstallerOptions
        {
            RuntimeIdentifier = package.Rid,
            Probe = (_, executable, _) => Task.FromResult<string?>(File.Exists(executable) ? package.Version : throw new FileNotFoundException(executable))
        });

        var result = await installer.InstallAsync(tool.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.ExecutablePath));
        Assert.Equal(package.Include?.Count ?? -1, package.Include is null ? -1 : Directory.EnumerateFiles(result.InstallDirectory, "*", SearchOption.AllDirectories).Count());
    }
}
