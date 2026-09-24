using System.Buffers.Binary;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Core.Tests.Media;

public class MotionPhotoLayoutTests
{
    [Fact]
    public void Inspect_GoogleMotionPhoto_LocatesVideoAtEnd()
    {
        var cover = SyntheticMedia.Jpeg(3000);
        var video = SyntheticMedia.Mp4(5000);
        var bytes = SyntheticMedia.MotionPhoto(cover, video);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(bytes));

        Assert.Equal(new EmbeddedVideo(bytes.Length - video.Length, video.Length), layout.Video);
        Assert.False(layout.HasGainMap);
    }

    [Fact]
    public void Inspect_MotionPhotoWithGainMap_KeepsGainMapInPhotoPart()
    {
        var gainMap = SyntheticMedia.Jpeg(700);
        var video = SyntheticMedia.Mp4(5000);
        var bytes = SyntheticMedia.MotionPhotoWithGainMap(gainMap, video);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(bytes));

        Assert.True(layout.HasGainMap);
        Assert.Equal(bytes.Length - video.Length, layout.Video?.Offset);
    }

    [Fact]
    public void Inspect_UltraHdrStill_IsNotMotionPhoto()
    {
        var layout = MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.UltraHdrStill(SyntheticMedia.Jpeg(4000))));

        Assert.Null(layout.Video);
        Assert.True(layout.HasGainMap);
    }

    [Fact]
    public void Inspect_StaleOffsetAfterEditing_IsRejected()
    {
        // 编辑软件重存照片时丢掉了尾部视频但保留了 XMP：按声明位置切割会切进封面，必须拒绝
        var bytes = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(30_000), SyntheticMedia.Mp4(5000));

        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(bytes[..^5000])).Video);
    }

    [Fact]
    public void Inspect_DeclaredLengthMismatch_IsRejected()
    {
        var declared = SyntheticMedia.MotionPhoto(SyntheticMedia.Jpeg(3000), SyntheticMedia.Mp4(5000));
        byte[] trimmed = [.. declared[..^5000], .. SyntheticMedia.Mp4(4000)];

        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(trimmed)).Video);
    }

    [Fact]
    public void Inspect_PlainJpeg_HasNoVideo()
    {
        Assert.Equal(default, MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.Jpeg())));
    }

    [Fact]
    public void Inspect_SamsungTrailerWithoutXmp_LocatesVideo()
    {
        var cover = SyntheticMedia.Jpeg(3000);
        var video = SyntheticMedia.Mp4(4000);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.SamsungMotionPhoto(cover, video)));

        Assert.Equal(cover.Length + 8 + "MotionPhoto_Data".Length, layout.Video?.Offset);
        Assert.Equal(video.Length, layout.Video?.Length);
    }

    [Fact]
    public void Inspect_SamsungTrailer_ImageEndsBeforeSefBlockHeader()
    {
        var cover = SyntheticMedia.Jpeg(3000);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.SamsungMotionPhoto(cover, SyntheticMedia.Mp4(4000))));

        Assert.Equal(cover.Length, layout.Video?.ImageEnd);
    }

    [Fact]
    public void Inspect_SamsungBlockShorterThanItsHeader_IsRejected()
    {
        var bytes = SyntheticMedia.SamsungMotionPhoto(SyntheticMedia.Jpeg(3000), SyntheticMedia.Mp4(4000));
        // 目录项记录的数据块长度小于 8 字节块头：无符号相减若回绕会得到约 4GB 的视频长度
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8 - 24 + 20), 4);

        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(bytes)).Video);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Inspect_HeicMpvdWithXmp_SeparatesImageEndFromVideoData(bool lengthIncludesBoxHeader)
    {
        var heic = SyntheticMedia.Heic(3000);
        var video = SyntheticMedia.Mp4(4000);
        var bytes = SyntheticMedia.HeicMotionPhoto(heic, video);
        var xmp = MotionPhotoXmp.Apply(null, video.Length + (lengthIncludesBoxHeader ? 8 : 0), 0);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(bytes), xmp);

        Assert.Equal(new EmbeddedVideo(heic.Length + 8, video.Length, heic.Length), layout.Video);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_HeicMpvdWithoutXmp_FoundByTopLevelBoxWalk(bool largeSize)
    {
        var heic = SyntheticMedia.Heic(3000);
        var video = SyntheticMedia.Mp4(4000);
        var headerLength = largeSize ? 16 : 8;

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.HeicMotionPhoto(heic, video, largeSize)));

        Assert.Equal(new EmbeddedVideo(heic.Length + headerLength, video.Length, heic.Length), layout.Video);
    }

    [Fact]
    public void Locate_HeicMpvdFileWithoutXmp_IsMotionPhoto()
    {
        using var temp = new TempDirectory();
        var heic = SyntheticMedia.Heic(3000);
        var path = temp.CreateFile("IMG.heic", SyntheticMedia.HeicMotionPhoto(heic, SyntheticMedia.Mp4(4000)));

        Assert.Equal(heic.Length, MotionPhotoLayout.Locate(path)?.ImageEnd);
    }

    [Fact]
    public void Inspect_PlainHeic_HasNoVideo()
    {
        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(SyntheticMedia.Heic(5000))).Video);
    }

    [Fact]
    public void Inspect_MpvdWithoutFtypInside_HasNoVideo()
    {
        var notVideo = new byte[4000];
        byte[] bytes = [.. SyntheticMedia.Heic(3000), .. SyntheticMedia.MpvdHeader(notVideo.Length), .. notVideo];

        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(bytes)).Video);
    }

    public static TheoryData<byte[]> MalformedBoxes()
    {
        var heic = SyntheticMedia.Heic(3000);
        byte[] Box(uint size, string type, int extra = 0)
        {
            var box = new byte[8 + extra];
            BinaryPrimitives.WriteUInt32BigEndian(box, size);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            return box;
        }

        byte[] LargeBox(ulong size)
        {
            var box = Box(1, "mpvd", 8);
            BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), size);
            return box;
        }

        byte[][] cases =
        [
            [.. heic, .. Box(3, "free")],                          // 长度小于 box 头
            [.. heic, .. Box(uint.MaxValue, "free"), 1, 2, 3],     // 长度超出文件
            [.. heic, .. Box(0, "free"), .. SyntheticMedia.MpvdHeader(100), .. SyntheticMedia.Mp4(100)], // size 0 延续到文件尾
            [.. heic, .. LargeBox(4)],                             // 64 位长度小于 box 头
            [.. heic, .. LargeBox(ulong.MaxValue)],                // 64 位长度溢出
            [.. heic, .. LargeBox(0)],
            [.. heic, 0, 0, 0],                                    // 不足一个 box 头的尾巴
            [0, 0, 0, 24, .. "ftyp"u8.ToArray()]                   // 只有截断的 ftyp
        ];
        var data = new TheoryData<byte[]>();
        foreach (var item in cases)
        {
            data.Add(item);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedBoxes))]
    public void Inspect_MalformedBoxLengths_ReturnsNoVideoWithoutThrowing(byte[] bytes)
    {
        Assert.Null(MotionPhotoLayout.Inspect(new MemoryStream(bytes)).Video);
    }

    [Fact]
    public void Locate_MissingFile_ReturnsNull()
    {
        Assert.Null(MotionPhotoLayout.Locate(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg")));
    }

    [Fact]
    public void ReadJpegXmp_NonJpeg_ReturnsNull()
    {
        Assert.Null(MotionPhotoLayout.ReadJpegXmp(new MemoryStream(SyntheticMedia.Heic())));
    }
}
