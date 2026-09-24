using LivePhotoConvert.Desktop.Services;

namespace LivePhotoConvert.Desktop.Tests.Services;

/// <summary>悬浮预览与 QuickLook 的 FFmpeg 解码参数及 BMP 帧管线。</summary>
public class PlaybackDecodeTests
{
    [Fact]
    public void PlaybackHost_DecodeArguments_PreserveSourceFramesWithoutForcedRate()
    {
        string[] arguments = PlaybackHost.BuildDecodeArguments(@"F:\媒体目录\IMG 5336.MOV");

        int inputIndex = Array.IndexOf(arguments, "-i");
        int fpsModeIndex = Array.IndexOf(arguments, "-fps_mode");

        Assert.True(inputIndex >= 0);
        Assert.Equal(@"F:\媒体目录\IMG 5336.MOV", arguments[inputIndex + 1]);
        Assert.True(fpsModeIndex >= 0);
        Assert.Equal("passthrough", arguments[fpsModeIndex + 1]);
        Assert.DoesNotContain("-r", arguments);
        Assert.DoesNotContain("mjpeg", arguments);
        Assert.DoesNotContain("-q:v", arguments);
        Assert.Contains("bmp", arguments);
        Assert.Contains("bgr24", arguments);
        Assert.Contains("scale=720:-2:flags=lanczos", arguments);
        Assert.Contains("-an", arguments);
        Assert.Contains("-sn", arguments);
        Assert.Contains("-dn", arguments);
    }

    [Fact]
    public void LivePhotoStreamPlayer_DecodeArguments_AvoidAutomaticHardwareAcceleration()
    {
        string[] arguments = LivePhotoStreamPlayer.BuildDecodeArguments(@"F:\媒体目录\IMG 5410.MOV");

        Assert.DoesNotContain("-hwaccel", arguments);
        Assert.Contains("scale=1080:1080:force_original_aspect_ratio=decrease:flags=lanczos", arguments);
        Assert.Contains("passthrough", arguments);
        Assert.Contains("bmp", arguments);
        Assert.Contains("bgr24", arguments);
    }

    [Fact]
    public async Task BmpPipeFrameReader_ReadsConsecutiveLosslessFrameBoundaries()
    {
        byte[] firstSource = CreateBmpPacket(54, 0x11);
        byte[] secondSource = CreateBmpPacket(70, 0x22);
        using var pipe = new MemoryStream([.. firstSource, .. secondSource]);

        byte[]? firstFrame = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);
        byte[]? secondFrame = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);
        byte[]? end = await BmpPipeFrameReader.ReadNextBytesAsync(pipe, CancellationToken.None);

        Assert.Equal(firstSource, firstFrame);
        Assert.Equal(secondSource, secondFrame);
        Assert.Null(end);
    }

    private static byte[] CreateBmpPacket(int length, byte payload)
    {
        byte[] bytes = Enumerable.Repeat(payload, length).ToArray();
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), length);
        return bytes;
    }
}
