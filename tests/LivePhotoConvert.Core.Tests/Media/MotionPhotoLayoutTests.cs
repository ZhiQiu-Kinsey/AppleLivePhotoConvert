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
    public void Inspect_HeicWithMpvdBox_SkipsBoxHeader()
    {
        var video = SyntheticMedia.Mp4(4000);
        var mpvd = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(mpvd, (uint)(video.Length + 8));
        "mpvd"u8.CopyTo(mpvd.AsSpan(4));
        byte[] bytes = [.. SyntheticMedia.Heic(3000), .. mpvd, .. video];
        var xmp = MotionPhotoXmp.Apply(null, video.Length + 8, 0);

        var layout = MotionPhotoLayout.Inspect(new MemoryStream(bytes), xmp);

        Assert.Equal(new EmbeddedVideo(bytes.Length - video.Length, video.Length), layout.Video);
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
