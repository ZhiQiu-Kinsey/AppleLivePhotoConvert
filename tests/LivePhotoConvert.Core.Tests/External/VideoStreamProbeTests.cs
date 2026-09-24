using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 真实 FFmpeg 探测：普通文件与 subfile 协议读取内嵌视频。
/// </summary>
public class VideoStreamProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProbeAsync_SubfileInPathWithSpecialCharacters_ReadsEmbeddedVideo()
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var video = temp.Combine("clip.mp4");
        await FfmpegSamples.H264Async(ffmpeg, video, "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
        // Windows 文件名不允许冒号；其它平台一并覆盖，冒号会被 FFmpeg 当作协议分隔符
        var name = OperatingSystem.IsWindows() ? "动态 照片,1.jpg" : "动态 照片,1:2.jpg";
        var container = temp.CreateFile(Path.Combine("相册 a,b", name));
        byte[] prefix = [.. Enumerable.Range(0, 4099).Select(i => (byte)i)];
        var videoBytes = await File.ReadAllBytesAsync(video, Token);
        await File.WriteAllBytesAsync(container, [.. prefix, .. videoBytes, .. prefix], Token);

        var info = await VideoStreamProbe.ProbeAsync(ffmpeg, VideoStreamProbe.SubfileInput(container, prefix.Length, videoBytes.Length), cancellationToken: Token);

        Assert.NotNull(info);
        Assert.Equal("h264", info.Codec);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal("bt709", info.ColorTransfer);
        Assert.Equal(TimeSpan.FromSeconds(1), info.Duration);
    }

    [Fact]
    public async Task ProbeAsync_MissingFile_ReturnsNull()
    {
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();

        Assert.Null(await VideoStreamProbe.ProbeAsync(ffmpeg, VideoStreamProbe.FileInput(temp.Combine("missing.mp4")), cancellationToken: Token));
    }
}
