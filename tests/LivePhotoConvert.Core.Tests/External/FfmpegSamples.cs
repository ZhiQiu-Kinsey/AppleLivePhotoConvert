using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>
/// 用本机 FFmpeg 的 lavfi 测试源生成样片；缺少 FFmpeg 或 libx265 时跳过。
/// </summary>
internal static class FfmpegSamples
{
    public const string MasterDisplay = "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<string> RequireLibx265Async(string ffmpeg)
    {
        var listed = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-encoders"], Token);
        if (!FfmpegVideoConverter.ParseEncoders(listed.StandardOutput).Contains("libx265"))
        {
            Assert.Skip("本机 FFmpeg 不带 libx265，跳过 HDR 集成测试。");
        }

        return ffmpeg;
    }

    /// <summary>HLG 或 PQ 的 10-bit HEVC；默认 hev1 标记，便于验证输出改为 hvc1。PQ 附带 HDR10 静态元数据。</summary>
    public static Task HdrAsync(string ffmpeg, string path, string transfer)
    {
        var x265 = transfer == VideoStreamProbe.TransferPq
            ? $"log-level=error:master-display={MasterDisplay}:max-cll=1000,400"
            : "log-level=error";
        return GenerateAsync(ffmpeg, path,
        [
            "-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-x265-params", x265,
            "-color_primaries", "bt2020", "-color_trc", transfer, "-colorspace", "bt2020nc", "-tag:v", "hev1"
        ]);
    }

    public static Task H264Async(string ffmpeg, string path, params string[] extra) =>
        GenerateAsync(ffmpeg, path, ["-c:v", "libx264", "-pix_fmt", "yuv420p", .. extra]);

    /// <summary>流复制并写入显示矩阵（逆时针 90°，即顺时针 270°）。</summary>
    public static async Task RotateAsync(string ffmpeg, string source, string destination)
    {
        var rotated = await ProcessRunner.RunAsync(ffmpeg,
            ["-nostdin", "-loglevel", "error", "-y", "-display_rotation", "90", "-i", source, "-c", "copy", destination], Token);
        if (!rotated.Success)
        {
            // FFmpeg 6.1 之前没有 -display_rotation，改用旧的 rotate 标签（顺时针）
            rotated = await ProcessRunner.RunAsync(ffmpeg,
                ["-nostdin", "-loglevel", "error", "-y", "-i", source, "-c", "copy", "-metadata:s:v:0", "rotate=270", destination], Token);
        }

        Assert.True(rotated.Success, rotated.StandardError);
    }

    /// <summary>视频流数据包的 MD5，用于判断是否为流复制。</summary>
    public static async Task<string> VideoPacketHashAsync(string ffmpeg, string path)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg, ["-nostdin", "-loglevel", "error", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "md5", "-"], Token);
        Assert.True(result.Success, result.StandardError);
        return result.StandardOutput.Trim();
    }

    public static async Task<VideoStreamInfo> ProbeAsync(string ffmpeg, string path, bool readFrameMetadata = false) =>
        await VideoStreamProbe.ProbeAsync(ffmpeg, VideoStreamProbe.FileInput(path), readFrameMetadata, Token)
        ?? throw new Xunit.Sdk.XunitException($"{path} 探测不到视频流");

    private static async Task GenerateAsync(string ffmpeg, string path, string[] videoArguments)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg,
        [
            "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
            "-t", "1", "-map", "0:v", "-map", "1:a", .. videoArguments, "-c:a", "aac", path
        ], Token);
        Assert.True(result.Success, result.StandardError);
    }
}
