using System.Buffers.Binary;
using System.Text;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class MovStillImageTrackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_WritesStillImageTimeTrackAtCoverTime()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("clip.MOV", TestMov.Build(movieTimescale: 1000, duration: 2000, width: 1008, height: 1344));

        var result = await MovStillImageTrack.AddAsync(path, 1_039_066, Token);

        Assert.Equal(StillImageTrackResult.Written, result);
        Assert.Equal(1_039_000, MovStillImageTrack.TryReadStillImageTimeUs(path));
        Assert.Equal(
            [
                "com.apple.quicktime.still-image-time",
                "com.apple.quicktime.live-photo-still-image-transform",
                "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions"
            ],
            TestMov.ReadMetadataKeyNames(path));
        Assert.Equal((1008, 1344), TestMov.ReadReferenceDimensions(path));
        Assert.Equal(2, TestMov.ReadNextTrackId(path));
        Assert.Equal([1, 2], TestMov.ReadTrackIds(path));
    }

    [Fact]
    public async Task Add_KeepsVideoSamplesReadableAtTheirNewOffsets()
    {
        using var temp = new TempDirectory();
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        var path = temp.CreateFile("clip.MOV", TestMov.Build(payload: payload, chunkOffset: TestMov.PayloadOffset, secondChunkOffset: TestMov.PayloadOffset + 2048));
        var original = await File.ReadAllBytesAsync(path, Token);
        var before = TestMov.ReadChunkOffsets(path);
        var samples = before.Select(offset => original.AsSpan((int)offset, 512).ToArray()).ToList();

        Assert.Equal(StillImageTrackResult.Written, await MovStillImageTrack.AddAsync(path, 500_000, Token));

        var bytes = await File.ReadAllBytesAsync(path, Token);
        // 末尾一条是新增轨道指向 105 字节样本的块偏移，前面仍是视频轨原有的块
        var after = TestMov.ReadChunkOffsets(path).Take(before.Count).ToList();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(samples[i], bytes.AsSpan((int)after[i], 512).ToArray());
        }
    }

    [Fact]
    public async Task Add_ClampsStillTimeInsideMovieDuration()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("clip.MOV", TestMov.Build(movieTimescale: 600, duration: 600, width: 640, height: 480));

        Assert.Equal(StillImageTrackResult.Written, await MovStillImageTrack.AddAsync(path, 9_000_000, Token));

        // 超出视频时长会指向不存在的画面，必须收敛到最后一帧之前（599/600 秒）
        Assert.Equal(998_333, MovStillImageTrack.TryReadStillImageTimeUs(path));
    }

    [Fact]
    public async Task Add_SecondCall_ReportsAlreadyPresent()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("clip.MOV", TestMov.Build());

        Assert.Equal(StillImageTrackResult.Written, await MovStillImageTrack.AddAsync(path, 250_000, Token));
        var first = await File.ReadAllBytesAsync(path, Token);

        Assert.Equal(StillImageTrackResult.AlreadyPresent, await MovStillImageTrack.AddAsync(path, 800_000, Token));
        Assert.Equal(first, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task Add_WithoutVideoTrack_LeavesFileUntouched()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("audio.MOV", TestMov.Build(video: false));
        var before = await File.ReadAllBytesAsync(path, Token);

        Assert.Equal(StillImageTrackResult.Unsupported, await MovStillImageTrack.AddAsync(path, 100_000, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
        Assert.Null(MovStillImageTrack.TryReadStillImageTimeUs(path));
    }

    [Fact]
    public async Task Add_WithoutMovieHeader_LeavesFileUntouched()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("broken.MOV", TestMov.Build(includeMovieHeader: false));
        var before = await File.ReadAllBytesAsync(path, Token);

        Assert.Equal(StillImageTrackResult.Unsupported, await MovStillImageTrack.AddAsync(path, 100_000, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(40)]
    public async Task Add_TruncatedFile_ReportsUnsupportedInsteadOfThrowing(int length)
    {
        using var temp = new TempDirectory();
        var bytes = TestMov.Build();
        var path = temp.CreateFile("cut.MOV", bytes.AsSpan(0, length).ToArray());

        // 畸形文件只应放弃写入，不能让异常穿透到条目层
        Assert.Equal(StillImageTrackResult.Unsupported, await MovStillImageTrack.AddAsync(path, 500_000, Token));
        Assert.Null(MovStillImageTrack.TryReadStillImageTimeUs(path));
    }

    [Fact]
    public async Task Add_MalformedTrak_DoesNotThrow()
    {
        using var temp = new TempDirectory();
        var bytes = TestMov.Build();
        // 把 tkhd 的 box 长度改成 1（64 位长度标记），使版本字节落点失去意义
        var tkhd = bytes.AsSpan().IndexOf("tkhd"u8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tkhd - 4), 1);
        var path = temp.CreateFile("bad.MOV", bytes);

        Assert.Equal(StillImageTrackResult.Unsupported, await MovStillImageTrack.AddAsync(path, 500_000, Token));
    }

    [Fact]
    public void TryReadStillImageTimeUs_PlainMov_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("plain.MOV", TestMov.Build());

        Assert.Null(MovStillImageTrack.TryReadStillImageTimeUs(path));
    }

    /// <summary>
    /// 构造结构完整的单视频轨 MOV：ftyp | mdat | moov(mvhd + trak)，trak 带 hdlr vide 与 stsd 尺寸。
    /// </summary>
    private static class TestMov
    {
        private const int SampleSize = 512;

        /// <summary>ftyp（8 字节头 + 12 字节内容）与 mdat 头（8 字节）之后即样本数据。</summary>
        public const int PayloadOffset = 28;

        public static byte[] Build(
            int movieTimescale = 1000,
            int duration = 2000,
            int width = 640,
            int height = 480,
            int chunkOffset = PayloadOffset,
            int? secondChunkOffset = null,
            byte[]? payload = null,
            bool video = true,
            bool includeMovieHeader = true)
        {
            payload ??= new byte[SampleSize * 2];
            var mvhd = includeMovieHeader
                ? FullBox("mvhd", 0, [.. U32(0), .. U32(0), .. U32(movieTimescale), .. U32(duration), .. new byte[80]])
                : [];
            var trak = Box("trak", [
                // tkhd v0 体共 84 字节：轨道号在 +12，时长在 +20，其后是保留字段、矩阵与宽高
                .. FullBox("tkhd", 0, [.. U32(0), .. U32(0), .. U32(1), .. U32(0), .. U32(duration), .. new byte[60]]),
                .. Box("mdia", [
                    .. FullBox("mdhd", 0, [.. U32(0), .. U32(0), .. U32(movieTimescale), .. U32(duration), .. U16(0x55C4), .. U16(0)]),
                    .. FullBox("hdlr", 0, [0, 0, 0, 0, .. Encoding.ASCII.GetBytes(video ? "vide" : "soun"), .. new byte[12], 0]),
                    .. Box("minf", Box("stbl", [
                        .. FullBox("stsd", 0, [.. U32(1), .. SampleEntry(video ? "hvc1" : "mp4a", width, height)]),
                        .. FullBox("stts", 0, [.. U32(1), .. U32(2), .. U32(SampleSize)]),
                        .. FullBox("stsc", 0, [.. U32(1), .. U32(1), .. U32(1), .. U32(1)]),
                        .. FullBox("stsz", 0, [.. U32(SampleSize), .. U32(2)]),
                        .. FullBox("stco", 0, [.. U32(secondChunkOffset is null ? 1 : 2), .. U32(chunkOffset), .. (secondChunkOffset is { } second ? U32(second) : [])])
                    ]))
                ])
            ]);

            return [.. Box("ftyp", [.. "qt  "u8, .. U32(0), .. "qt  "u8]), .. Box("mdat", payload), .. Box("moov", mvhd.Length > 0 ? [mvhd, trak] : [trak])];
        }

        /// <summary>VisualSampleEntry：8 字节头 + 6 保留 + 2 索引 + 16 固定字段 + 宽高。</summary>
        private static byte[] SampleEntry(string type, int width, int height) =>
            Box(type, [.. new byte[6], .. U16(1), .. new byte[16], .. U16((ushort)width), .. U16((ushort)height), .. new byte[32], .. U16(1), .. new byte[32], .. U16(0x18), .. U16(0xFFFF)]);

        public static IReadOnlyList<string> ReadMetadataKeyNames(string path) =>
            ReadTrakStrings(path, "keyd").Select(text => text.TrimStart('\0')).ToList();

        public static (int Width, int Height) ReadReferenceDimensions(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // 样本紧挨在 moov 之前：记录 3 的两个 float 位于样本内 97 与 101 字节
            var sample = bytes.AsSpan(BoxOf(bytes, "moov").Offset - 105, 105);
            Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(sample[89..]));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(sample[93..]));
            return (ReadFloat(sample[97..]), ReadFloat(sample[101..]));
        }

        public static int ReadNextTrackId(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var moov = BoxOf(bytes, "moov");
            var mvhd = BoxOf(bytes.AsSpan(moov.Offset, moov.Length).ToArray(), "mvhd");
            return (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(moov.Offset + mvhd.Offset + 8 + 96));
        }

        public static IReadOnlyList<int> ReadTrackIds(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var moov = BoxOf(bytes, "moov");
            var ids = new List<int>();
            var position = moov.Offset + 8;
            while (position + 8 <= moov.Offset + moov.Length)
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
                if (bytes.AsSpan(position + 4, 4).SequenceEqual("trak"u8))
                {
                    var tkhd = BoxOf(bytes.AsSpan(position, size).ToArray(), "tkhd");
                    ids.Add((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + tkhd.Offset + 8 + 12)));
                }

                position += size;
            }

            return ids;
        }

        public static IReadOnlyList<uint> ReadChunkOffsets(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var offsets = new List<uint>();
            var index = 0;
            while ((index = Find(bytes, "stco"u8, index)) >= 0)
            {
                var boxStart = index - 4;
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(boxStart + 12));
                for (var i = 0; i < count; i++)
                {
                    offsets.Add(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(boxStart + 16 + i * 4)));
                }

                index += 4;
            }

            return offsets;
        }

        private static IEnumerable<string> ReadTrakStrings(string path, string boxType)
        {
            var bytes = File.ReadAllBytes(path);
            var marker = Encoding.ASCII.GetBytes(boxType);
            var index = 0;
            while ((index = Find(bytes, marker, index)) >= 0)
            {
                // keyd 的内容是命名空间（4 字节）加键名
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(index - 4));
                yield return Encoding.ASCII.GetString(bytes, index + 8, Math.Max(0, length - 12));
                index += 4;
            }
        }

        private static int Find(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value, int start)
        {
            var index = data[start..].IndexOf(value);
            return index < 0 ? -1 : start + index;
        }

        private static (int Offset, int Length) BoxOf(byte[] data, string type)
        {
            var index = Find(data, Encoding.ASCII.GetBytes(type), 0);
            Assert.True(index >= 4, $"找不到 {type} box");
            var offset = index - 4;
            return (offset, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
        }

        private static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        private static byte[] Box(string type, byte[][] children) => Box(type, children.SelectMany(child => child).ToArray());

        private static byte[] FullBox(string type, byte version, byte[] body) => Box(type, [version, 0, 0, 0, .. body]);

        private static int ReadFloat(ReadOnlySpan<byte> data) =>
            (int)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));

        private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

        private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }
}
