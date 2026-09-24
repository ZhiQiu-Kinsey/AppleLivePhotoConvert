using LivePhotoConvert.Desktop.Features.Playback;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

public class ShowInfoParserTests
{
    // FFmpeg 6.1 实际输出（节选），含时间基配置行、帧行、附加数据与色彩行
    private const string Ffmpeg61 = """
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] config in time_base: 1/15360, frame_rate: 30/1
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] config out time_base: 0/0, frame_rate: 0/0
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] n:   0 pts:      0 pts_time:0       duration:    512 duration_time:0.0333333 fmt:bgra cl:left sar:1/1 s:160x120 i:P iskey:1 type:I
        [Parsed_showinfo_2 @ 0x55f7a3bdd940]   side data - H.26[45] User Data Unregistered SEI message: UUID=dc45e9bd-e6d9-48b7-962c-d820d923eeef
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] color_range:pc color_space:gbr color_primaries:unknown color_trc:unknown
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] n:   1 pts:    512 pts_time:0.0333333 duration:    512 duration_time:0.0333333 fmt:bgra cl:left sar:1/1 s:160x120 i:P iskey:0 type:B
        [Parsed_showinfo_2 @ 0x55f7a3bdd940] n:   2 pts:   1536 pts_time:0.1     duration:    512 duration_time:0.0333333 fmt:bgra cl:left sar:1/1 s:160x120 i:P iskey:0 type:P
        Output #0, rawvideo, to 'pipe:':
        """;

    [Fact]
    public void TryParse_Ffmpeg61Output_UsesExactTimeBase()
    {
        var frames = ParseAll(new ShowInfoParser(), Ffmpeg61);

        Assert.Equal([0L, 1L, 2L], frames.Select(f => f.Index));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromTicks(333_333), TimeSpan.FromMilliseconds(100)], frames.Select(f => f.Pts!.Value));
        Assert.All(frames, f => Assert.Equal(TimeSpan.FromTicks(333_333), f.Duration));
    }

    [Fact]
    public void TryParse_NinetyKilohertzTimeBase_KeepsPrecisionBeyondPtsTime()
    {
        var parser = new ShowInfoParser();
        Assert.False(parser.TryParse("[Parsed_showinfo_1 @ 0x1] config in time_base: 1/90000, frame_rate: 30000/1001", out _));

        // pts_time 只有 6 位有效数字（1000.03），换算应基于整数 pts
        Assert.True(parser.TryParse("[Parsed_showinfo_1 @ 0x1] n: 30000 pts:90003003 pts_time:1000.03 duration:   3003 duration_time:0.0333667 fmt:bgra", out var frame));

        Assert.Equal(30000, frame.Index);
        Assert.Equal(TimeSpan.FromTicks(10_000_333_666), frame.Pts);
        Assert.Equal(TimeSpan.FromTicks(333_666), frame.Duration);
    }

    [Fact]
    public void TryParse_OldFormatWithoutConfigAndDuration_FallsBackToPtsTime()
    {
        var parser = new ShowInfoParser();

        Assert.True(parser.TryParse("[Parsed_showinfo_1 @ 0x7f] n:  12 pts:   6144 pts_time:0.4     pos:    48213 fmt:yuv420p sar:1/1 s:320x240 i:P iskey:0 type:P checksum:0A1B2C3D", out var frame));

        Assert.Equal(12, frame.Index);
        Assert.Equal(TimeSpan.FromMilliseconds(400), frame.Pts);
        Assert.Null(frame.Duration);
    }

    [Fact]
    public void TryParse_NoPts_ReturnsFrameWithoutTimestamp()
    {
        Assert.True(new ShowInfoParser().TryParse("[Parsed_showinfo_0 @ 0x1] n:   5 pts:NOPTS pts_time:NOPTS duration:NOPTS duration_time:NOPTS fmt:bgra", out var frame));

        Assert.Equal(5, frame.Index);
        Assert.Null(frame.Pts);
        Assert.Null(frame.Duration);
    }

    [Fact]
    public void TryParse_LineMixedWithProgressOutput_StillParses()
    {
        // 未加 -nostats 时进度以 \r 结尾，按行读取会与下一条帧信息拼在一起
        const string line = "frame=    0 fps=0.0 q=-0.0 size=       0kB time=00:00:00.00 bitrate=N/A speed=   0x    [Parsed_showinfo_7 @ 0x559d20cdb0c0] n:   1 pts:    512 pts_time:0.0333333 duration:    512 duration_time:0.0333333 fmt:bgra";

        Assert.True(new ShowInfoParser().TryParse(line, out var frame));
        Assert.Equal(1, frame.Index);
    }

    [Theory]
    [InlineData("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'a.mp4':")]
    [InlineData("  Stream #0:0[0x1](und): Video: h264 (High), yuv420p(progressive), 320x240, 30 fps")]
    [InlineData("[Parsed_showinfo_2 @ 0x1] color_range:tv color_space:bt709 color_primaries:bt709 color_trc:bt709")]
    [InlineData("[Parsed_showinfo_2 @ 0x1]   side data - H.26[45] User Data Unregistered SEI message")]
    public void TryParse_OtherLines_AreIgnored(string line) => Assert.False(new ShowInfoParser().TryParse(line, out _));

    private static List<ShowInfoFrame> ParseAll(ShowInfoParser parser, string text)
    {
        List<ShowInfoFrame> frames = [];
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (parser.TryParse(line, out var frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }
}
