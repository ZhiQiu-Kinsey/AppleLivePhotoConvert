using ImageMagick;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Metadata;

/// <summary>
/// 使用真实 ExifTool 的集成测试；未安装时跳过。
/// </summary>
public class ExifToolMetadataServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadAsync_BatchReadsPhotoAndVideo_WithUnicodePaths()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var photo = CreateJpeg(temp, "中文 目录/IMG 0001.jpg");
        var video = temp.Combine("中文 目录", "IMG 0001.mov");
        await RunAsync(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=30", "-t", "1", "-pix_fmt", "yuv420p",
            "-metadata", "creation_time=2024-05-01T06:03:02Z", "-movflags", "use_metadata_tags",
            "-metadata", "com.apple.quicktime.content.identifier=UUID-ABC", "-metadata", "com.apple.quicktime.creationdate=2024-05-01T14:03:02+0800", video);
        await RunAsync(exiftool, "-q", "-overwrite_original", "-DateTimeOriginal=2024:05:01 14:03:03", "-OffsetTimeOriginal=+08:00", photo);

        await using var service = ExifToolMetadataService.Create(exiftool);
        var tags = await service.ReadAsync([photo, video, temp.Combine("missing.jpg")], cancellationToken: Token);

        Assert.Equal(TimeSpan.FromHours(8), tags[photo].CaptureTime?.Offset);
        Assert.Equal("UUID-ABC", tags[video].ContentIdentifier);
        Assert.Equal(TimeSpan.FromSeconds(1), tags[photo].CaptureTime!.Value.DistanceTo(tags[video].CaptureTime!.Value));
        Assert.Equal(1.0, tags[video].Duration!.Value.TotalSeconds, precision: 1);
        Assert.Null(tags[temp.Combine("missing.jpg")].CaptureTime);
    }

    [Fact]
    public async Task WriteMotionPhotoAsync_PreservesExistingXmpAndMarksXiaomiTag()
    {
        var exiftool = ExternalTools.RequireExifTool();
        using var temp = new TempDirectory();
        var cover = CreateJpeg(temp, "cover.jpg");
        await RunAsync(exiftool, "-q", "-overwrite_original", "-XMP-dc:Subject=keep-me", cover);

        await using var service = ExifToolMetadataService.Create(exiftool);
        await service.WriteMotionPhotoAsync(cover, 4096, 1_500_000, Token);

        var xmp = await service.ReadXmpAsync(cover, Token);
        Assert.Contains("keep-me", xmp);
        Assert.Equal((4096L, 0L), MotionPhotoXmp.Parse(xmp)?.VideoExtent);
        var microVideo = await RunAsync(exiftool, "-config", Path.Combine(Path.GetTempPath(), "LivePhotoConvert", "LivePhotoExif.config"), "-s3", "-EXIF:MicroVideo", cover);
        Assert.Equal("1", microVideo.StandardOutput.Trim());

        await service.RemoveMotionPhotoAsync(cover, Token);
        var stripped = await service.ReadXmpAsync(cover, Token);
        Assert.Contains("keep-me", stripped);
        Assert.Null(MotionPhotoXmp.Parse(stripped));
    }

    [Fact]
    public async Task WriteApplePhotoIdentifierAsync_CreatesAppleMakerNotes()
    {
        var exiftool = ExternalTools.RequireExifTool();
        using var temp = new TempDirectory();
        var photo = CreateJpeg(temp, "IMG_0001.jpg");

        await using var service = ExifToolMetadataService.Create(exiftool);
        await service.WriteApplePhotoIdentifierAsync(photo, "0E1B2C3D-AAAA-BBBB-CCCC-DDDDEEEEFFFF", Token);

        var tags = await service.ReadAsync([photo], cancellationToken: Token);
        Assert.Equal("0E1B2C3D-AAAA-BBBB-CCCC-DDDDEEEEFFFF", tags[photo].ContentIdentifier);
    }

    [Fact]
    public async Task WriteAppleVideoTagsAsync_WritesIdentifierTimeAndLocation()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var video = temp.Combine("IMG_0001.MOV");
        await RunAsync(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=30", "-t", "1", "-pix_fmt", "yuv420p", "-f", "mov", video);
        CaptureTime.TryParse("2024:05:01 14:03:03+08:00", out var captureTime);

        await using var service = ExifToolMetadataService.Create(exiftool);
        await service.WriteAppleVideoTagsAsync(video, new AppleVideoTags("UUID-XYZ") { CaptureTime = captureTime, Location = new GeoLocation(31.2, 121.4, 5), Make = "Apple" }, Token);

        var tags = (await service.ReadAsync([video], cancellationToken: Token))[video];
        Assert.Equal("UUID-XYZ", tags.ContentIdentifier);
        Assert.Equal(TimeSpan.Zero, tags.CaptureTime!.Value.DistanceTo(captureTime));
        var quickTimeUtc = await RunAsync(exiftool, "-s3", "-QuickTime:CreateDate", video);
        Assert.Equal("2024:05:01 06:03:03", quickTimeUtc.StandardOutput.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPathsWithLineBreaks()
    {
        var exiftool = ExternalTools.RequireExifTool();
        await using var service = ExifToolMetadataService.Create(exiftool);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadXmpAsync("a\n-delete_original!.heic", Token));
    }

    private static string CreateJpeg(TempDirectory temp, string name)
    {
        var path = temp.CreateFile(name);
        using var image = new MagickImage(MagickColors.SteelBlue, 64, 48);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static async Task<ProcessResult> RunAsync(string tool, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(tool, arguments, Token);
        Assert.True(result.Success, result.StandardError);
        return result;
    }
}
