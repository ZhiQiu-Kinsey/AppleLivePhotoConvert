using ImageMagick;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Io;
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

    [Fact]
    public async Task ReadXmpAsync_Heic_ReturnsBinaryXmpPromptlyAndSessionStaysUsable()
    {
        var exiftool = ExternalTools.RequireExifTool();
        using var temp = new TempDirectory();
        var heic = await CreateHeicAsync(temp, "IMG_0001.heic");
        await RunAsync(exiftool, "-q", "-overwrite_original", "-XMP-dc:Subject=keep-me", heic);
        var jpeg = CreateJpeg(temp, "IMG_0002.jpg");
        await RunAsync(exiftool, "-q", "-overwrite_original", "-DateTimeOriginal=2024:05:01 14:03:03", jpeg);

        // 单个会话：-b 的输出若没有正确收尾，后续命令会读到错位的内容
        await using var service = ExifToolMetadataService.Create(exiftool, maxSessions: 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var xmp = await service.ReadXmpAsync(heic, timeout.Token);
        var tags = await service.ReadAsync([jpeg, heic], cancellationToken: timeout.Token);
        var again = await service.ReadXmpAsync(heic, timeout.Token);

        Assert.NotNull(xmp);
        Assert.Contains("keep-me", xmp);
        Assert.DoesNotContain("{ready", xmp, StringComparison.Ordinal);
        Assert.Equal(xmp, again);
        Assert.Equal(new DateTime(2024, 5, 1, 14, 3, 3), tags[jpeg].CaptureTime?.LocalTime);
    }

    [Fact]
    public async Task ReadXmpAsync_HeicWithoutXmp_ReturnsNullPromptly()
    {
        var exiftool = ExternalTools.RequireExifTool();
        using var temp = new TempDirectory();
        var heic = await CreateHeicAsync(temp, "plain.heic");

        await using var service = ExifToolMetadataService.Create(exiftool, maxSessions: 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        Assert.Null(await service.ReadXmpAsync(heic, timeout.Token));
    }

    [Fact]
    public async Task RemoveMotionPhotoAsync_HeicTruncatedAtMpvdBox_Succeeds()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var heic = await File.ReadAllBytesAsync(await CreateHeicAsync(temp, "cover.heic"), Token);
        var video = temp.Combine("clip.mp4");
        await RunAsync(ffmpeg, "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=30", "-t", "1", "-pix_fmt", "yuv420p", video);
        var source = temp.CreateFile("MVIMG_0001.heic", SyntheticMedia.HeicMotionPhoto(heic, await File.ReadAllBytesAsync(video, Token)));

        var located = MotionPhotoLayout.Locate(source);
        Assert.NotNull(located);
        var clean = temp.Combine("clean.heic");
        await BinaryFile.CopySegmentAsync(source, clean, 0, located.ImageEnd, Token);

        await using var service = ExifToolMetadataService.Create(exiftool);
        await service.RemoveMotionPhotoAsync(clean, Token);

        // 多截 8 字节 mpvd 头时 ExifTool 会报 Truncated 'mpvd' data
        var check = await RunAsync(exiftool, "-s3", "-validate", clean);
        Assert.Equal("OK", check.StandardOutput.Trim());
    }

    /// <summary>用 heif-enc 生成真实 HEIC（顶层 box 为 ftyp/meta/mdat）。</summary>
    private static async Task<string> CreateHeicAsync(TempDirectory temp, string name)
    {
        var heifEnc = ExternalTools.RequireHeifEnc();
        var png = temp.CreateFile(Path.ChangeExtension(name, ".png"));
        using (var image = new MagickImage(MagickColors.SteelBlue, 64, 48))
        {
            image.Write(png, MagickFormat.Png);
        }

        var heic = temp.Combine(name);
        await RunAsync(heifEnc, "-q", "50", png, "-o", heic);
        File.Delete(png);
        return heic;
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
