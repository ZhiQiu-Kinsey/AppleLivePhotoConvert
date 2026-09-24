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
}
