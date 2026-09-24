using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 旧下载入口的兼容层与工具定位。
/// </summary>
public class ToolDownloaderTests
{
    [Fact]
    public void GetWritableToolDirectory_ReturnsExistingDirectory()
    {
        var directory = ToolDownloader.GetWritableToolDirectory();

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void LocalAppDataToolDirectory_IsUnderLivePhotoConvert()
    {
        var directory = ToolDownloader.LocalAppDataToolDirectory;

        Assert.Contains("LivePhotoConvert", directory);
        Assert.EndsWith("tools", directory);
    }

    [Theory]
    [InlineData(ToolId.ExifTool)]
    [InlineData(ToolId.Ffmpeg)]
    [InlineData(ToolId.HeifEnc)]
    public void ExternalToolMetadata_SourcesComeFromManifest(ToolId id)
    {
        var info = id switch
        {
            ToolId.ExifTool => ExternalToolMetadata.ExifTool,
            ToolId.Ffmpeg => ExternalToolMetadata.FFmpeg,
            _ => ExternalToolMetadata.HeifEnc
        };

        Assert.Equal(id, info.Id);
        Assert.Equal(
            ToolManifest.Embedded.Get(id).PackagesFor("win-x64").Select(package => package.Url),
            info.Sources.Select(source => source.Url));
        Assert.All(info.Sources, source => Assert.StartsWith("https://", source.Url, StringComparison.Ordinal));
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
