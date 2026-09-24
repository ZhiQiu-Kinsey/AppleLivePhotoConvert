using System.Runtime.Versioning;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>工具目录选择与工具定位。</summary>
public class ToolLocatorTests
{
    [Fact]
    public void GetWritableToolDirectory_ReturnsExistingDirectory()
    {
        var directory = ToolDirectories.GetWritableToolDirectory();

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void LocalAppDataToolDirectory_IsUnderLivePhotoConvert()
    {
        var directory = ToolDirectories.LocalAppDataToolDirectory;

        Assert.Contains("LivePhotoConvert", directory);
        Assert.EndsWith("tools", directory);
    }

    [Fact]
    public void ToolLocator_Find_DiscoversExecutableInToolsSubdirectory()
    {
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(toolsDir);
        var dummyName = $"dummy_tool_{Guid.NewGuid():N}.exe";
        var dummyPath = Path.Combine(toolsDir, dummyName);
        File.WriteAllText(dummyPath, "test");
        try
        {
            Assert.Equal(Path.GetFullPath(dummyPath), ToolLocator.Find(dummyName));
        }
        finally
        {
            File.Delete(dummyPath);
        }
    }

    [Fact]
    public void ToolLocator_Find_DiscoversInstallerLayout()
    {
        var stem = $"dummy_tool_{Guid.NewGuid():N}";
        var fileName = stem + ".exe";
        var installDirectory = Path.Combine(AppContext.BaseDirectory, "tools", stem);
        Directory.CreateDirectory(installDirectory);
        var path = Path.Combine(installDirectory, fileName);
        File.WriteAllText(path, "test");
        try
        {
            Assert.Equal(Path.GetFullPath(path), ToolLocator.Find(fileName));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void ToolLocator_Find_DiscoversCompanionInSiblingInstallDirectory()
    {
        // heif-dec 随 heif-enc 的发行包一起装在 tools/heif-enc/ 下，按子目录名查找
        var fileName = $"dummy_tool_{Guid.NewGuid():N}.exe";
        var sibling = $"dummy_pkg_{Guid.NewGuid():N}";
        var installDirectory = Path.Combine(AppContext.BaseDirectory, "tools", sibling);
        Directory.CreateDirectory(installDirectory);
        var path = Path.Combine(installDirectory, fileName);
        File.WriteAllText(path, "test");
        try
        {
            Assert.Null(ToolLocator.Find(fileName));
            Assert.Equal(Path.GetFullPath(path), ToolLocator.Find(fileName, null, sibling));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void IsValidTool_LaunchFailure_IsNotCachedAndRecoversOnceRunnable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "用可执行位模拟暂时无法启动，仅适用于类 Unix 系统。");
        using var temp = new TempDirectory();
        // 名称含 ffmpeg 才会真正启动进程探测；改可执行位不改变大小与修改时间，缓存键不变
        var tool = temp.CreateFile($"ffmpeg-{Guid.NewGuid():N}", "#!/bin/sh\nexit 0\n"u8.ToArray());
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.False(ToolLocator.IsValidTool(tool));

        File.SetUnixFileMode(tool, File.GetUnixFileMode(tool) | UnixFileMode.UserExecute);
        Assert.True(ToolLocator.IsValidTool(tool));
    }
}
