using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>工具安装根目录的选择，以及程序目录由更新器管理时从旧位置迁移工具。</summary>
public class ToolDirectoriesTests
{
    [Fact]
    public void ProgramDirectoryReplacedByUpdater_InstallsToLocalAppData()
    {
        var directory = ToolDirectories.GetWritableToolDirectory(programDirectoryIsReplaced: true);

        Assert.Equal(Path.GetFullPath(ToolDirectories.LocalAppDataToolDirectory), Path.GetFullPath(directory));
        Assert.True(Directory.Exists(directory));
        Assert.DoesNotContain(Path.GetFullPath(directory), ToolDirectories.ProgramToolRoots());
    }

    [Fact]
    public void PortableDeployment_PrefersWritableProgramDirectory()
    {
        var directory = ToolDirectories.GetWritableToolDirectory(programDirectoryIsReplaced: false);

        Assert.Contains(Path.GetFullPath(directory), ToolDirectories.ProgramToolRoots());
        Assert.Contains(Path.GetFullPath(directory), ToolDirectories.CandidateToolRoots());
    }

    [Fact]
    public void LegacyRoots_IncludeProgramToolsAndParentTools()
    {
        var legacy = ToolDirectories.LegacyToolRoots();
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;

        Assert.All(ToolDirectories.ProgramToolRoots(), root => Assert.Contains(root, legacy));
        Assert.Contains(Path.GetFullPath(Path.Combine(parent, "tools")), legacy);
    }

    [Fact]
    public void Migrate_CopiesMissingEntries_KeepsSourceAndExistingTargets()
    {
        using var temp = new TempDirectory();
        var legacy = temp.Combine("app", "tools");
        var target = temp.Combine("data", "tools");
        temp.CreateFile(Path.Combine("app", "tools", "ffmpeg", "bin", "ffmpeg.exe"), [1, 2, 3, 4]);
        temp.CreateFile(Path.Combine("app", "tools", "ffmpeg", "LICENSE"), [5]);
        temp.CreateFile(Path.Combine("app", "tools", "exiftool.exe"), [6, 7]);
        temp.CreateFile(Path.Combine("app", "tools", "heif-enc", "heif-enc.exe"), [8]);
        temp.CreateFile(Path.Combine("app", "tools", ".staging-ffmpeg-1", "partial"), [9]);
        // 目标已有的工具（例如新版已经重新下载过）不被旧副本覆盖
        temp.CreateFile(Path.Combine("data", "tools", "heif-enc", "heif-enc.exe"), [10, 11]);

        var migrated = ToolDirectories.MigrateLegacyTools([legacy, target, temp.Combine("missing")], target);

        Assert.Equal(["exiftool.exe", "ffmpeg"], migrated.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(target, "ffmpeg", "bin", "ffmpeg.exe")));
        Assert.Equal([5], File.ReadAllBytes(Path.Combine(target, "ffmpeg", "LICENSE")));
        Assert.Equal([6, 7], File.ReadAllBytes(Path.Combine(target, "exiftool.exe")));
        Assert.Equal([10, 11], File.ReadAllBytes(Path.Combine(target, "heif-enc", "heif-enc.exe")));
        Assert.False(Directory.Exists(Path.Combine(target, ".staging-ffmpeg-1")));
        Assert.Empty(Directory.EnumerateDirectories(target, ".migrate-*"));

        // 源保留：程序目录下的副本随下次更新一起被替换，迁移期间正在使用它的进程不受影响
        Assert.True(File.Exists(Path.Combine(legacy, "ffmpeg", "bin", "ffmpeg.exe")));

        // 再次迁移没有新条目
        Assert.Empty(ToolDirectories.MigrateLegacyTools([legacy], target));
    }

    [Fact]
    public void Migrate_RemovesStaleStagingLeftByInterruptedRun_KeepsFreshOne()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("data", "tools");
        var stale = Path.GetDirectoryName(temp.CreateFile(Path.Combine("data", "tools", ".migrate-old", "ffmpeg", "ffmpeg.exe"), [1]))!;
        var staleRoot = Path.GetDirectoryName(stale)!;
        Directory.SetLastWriteTimeUtc(staleRoot, DateTime.UtcNow.AddDays(-2));
        // 另一个实例刚创建、仍在复制的暂存目录不能被删
        var fresh = Path.GetDirectoryName(temp.CreateFile(Path.Combine("data", "tools", ".migrate-new", "exiftool.exe"), [2]))!;

        ToolDirectories.MigrateLegacyTools([temp.Combine("missing")], target);

        Assert.False(Directory.Exists(staleRoot));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Migrate_Failure_IsReportedAndSkipped()
    {
        using var temp = new TempDirectory();
        var legacy = temp.Combine("app", "tools");
        temp.CreateFile(Path.Combine("app", "tools", "ffmpeg", "ffmpeg.exe"), [1]);
        // 目标根目录是一个文件：无法创建暂存目录
        var target = temp.CreateFile("data-tools-is-a-file");
        var errors = new List<string>();

        var migrated = ToolDirectories.MigrateLegacyTools([legacy], target, (path, _) => errors.Add(path));

        Assert.Empty(migrated);
        Assert.Equal([Path.Combine(legacy, "ffmpeg")], errors);
        Assert.True(File.Exists(Path.Combine(legacy, "ffmpeg", "ffmpeg.exe")));
    }
}
