using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 外部工具下载元数据与路径解析测试
/// </summary>
public class ToolDownloaderTests
{
    /// <summary>
    /// 测试 ExifTool 的 Zip 条目筛选器能够正确识别 exiftool.exe 和 exiftool(-k).exe
    /// </summary>
    [Theory]
    [InlineData("exiftool(-k).exe", true)]
    [InlineData("exiftool.exe", true)]
    [InlineData("exiftool-13.25/exiftool(-k).exe", true)]
    [InlineData("exiftool-13.25/exiftool.exe", true)]
    [InlineData("exiftool-13.25\\exiftool(-k).exe", true)]
    [InlineData("readme.txt", false)]
    [InlineData("exiftool.html", false)]
    public void ExifTool_ZipEntryFilter_Should_Match_Executable(string entryName, bool shouldMatch)
    {
        var matched = ExternalToolMetadata.ExifTool.ZipEntryFilter(entryName);
        Assert.Equal(shouldMatch, matched);
    }

    /// <summary>
    /// 测试 FFmpeg 的 Zip 条目筛选器能够正确识别 bin 目录下的 ffmpeg.exe
    /// </summary>
    [Theory]
    [InlineData("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", true)]
    [InlineData("ffmpeg-release-essentials/bin/ffmpeg.exe", true)]
    [InlineData("bin\\ffmpeg.exe", true)]
    [InlineData("ffmpeg.exe", true)]
    [InlineData("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", false)]
    [InlineData("ffmpeg-master-latest-win64-gpl/bin/ffplay.exe", false)]
    [InlineData("doc/ffmpeg.html", false)]
    public void FFmpeg_ZipEntryFilter_Should_Match_Only_FFmpeg_Executable(string entryName, bool shouldMatch)
    {
        var matched = ExternalToolMetadata.FFmpeg.ZipEntryFilter(entryName);
        Assert.Equal(shouldMatch, matched);
    }

    /// <summary>
    /// 测试当传入用户自定义镜像前缀时，GitHub Release 源会被加上加速前缀并优先排在前面
    /// </summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "测试用固定 URL 前缀")]
    public void GetEffectiveSources_With_Custom_Mirror_Should_Prepend_Accelerated_Sources()
    {
        var customMirror = "https://custom-mirror.example.com/";
        var sources = ExternalToolMetadata.FFmpeg.GetEffectiveSources(customMirror);

        // 应该包含添加了自定义前缀的项
        Assert.Contains(sources, s => s.Url.StartsWith(customMirror, StringComparison.Ordinal));
        // 第一项应该是自定义镜像源
        Assert.StartsWith(customMirror, sources[0].Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// 测试不提供自定义镜像时，默认返回预置的源列表
    /// </summary>
    [Fact]
    public void GetEffectiveSources_Without_Custom_Mirror_Should_Return_Default_Sources()
    {
        var sources = ExternalToolMetadata.ExifTool.GetEffectiveSources();
        Assert.Equal(ExternalToolMetadata.ExifTool.Sources.Count, sources.Count);
    }

    /// <summary>
    /// 测试 GetWritableToolDirectory 返回有效的非空目录路径
    /// </summary>
    [Fact]
    public void GetWritableToolDirectory_Should_Return_Valid_Directory()
    {
        var directory = ToolDownloader.GetWritableToolDirectory();

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// 测试 LocalAppDataToolDirectory 包含 LivePhotoConvert 路径特征
    /// </summary>
    [Fact]
    public void LocalAppDataToolDirectory_Should_Be_Under_LocalAppData()
    {
        var directory = ToolDownloader.LocalAppDataToolDirectory;

        Assert.Contains("LivePhotoConvert", directory);
        Assert.Contains("tools", directory);
    }

    /// <summary>
    /// 测试 ExifTool 和 FFmpeg 的所有预置下载源均配置了合法的 HTTPS URL 与非空名称
    /// </summary>
    [Theory]
    [InlineData("ExifTool")]
    [InlineData("FFmpeg")]
    public void DownloadSources_Should_Have_Valid_Https_Urls(string toolName)
    {
        var tool = toolName == "ExifTool" ? ExternalToolMetadata.ExifTool : ExternalToolMetadata.FFmpeg;

        Assert.NotEmpty(tool.Sources);
        foreach (var source in tool.Sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
            Assert.True(Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                $"源 [{source.Name}] 的 URL 不是合法的 HTTPS 链接: {source.Url}");
        }
    }
}
