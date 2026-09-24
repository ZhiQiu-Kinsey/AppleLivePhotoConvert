using ImageMagick;
using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

/// <summary>
/// 用真实 ExifTool 与 FFmpeg 走完「合成 → 拆分 → 还原」全流程；缺少工具时跳过。
/// </summary>
public class RealToolsRoundTripTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MergeThenRestore_ProducesLivePhotoPairWithMatchingIdentifiers()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var ffmpeg = ExternalTools.RequireFfmpeg();
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("相册/IMG_0001.jpg");
        using (var image = new MagickImage(MagickColors.OrangeRed, 96, 64))
        {
            image.Write(photo, MagickFormat.Jpeg);
        }

        var video = temp.Combine("相册", "IMG_0001.mov");
        var generated = await ProcessRunner.RunAsync(ffmpeg,
        [
            "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=96x64:rate=30", "-t", "1", "-pix_fmt", "yuv420p",
            "-movflags", "use_metadata_tags", "-metadata", "com.apple.quicktime.content.identifier=SRC-ID", video
        ], Token);
        Assert.True(generated.Success, generated.StandardError);
        await ProcessRunner.RunAsync(exiftool, ["-q", "-overwrite_original", "-ContentIdentifier=SRC-ID", photo], Token);

        await using var metadata = ExifToolMetadataService.Create(exiftool, maxSessions: 2);
        var videos = FfmpegVideoConverter.Create(ffmpeg);
        IImageConverter images = ToolLocator.Find(HeifEncImageConverter.ExecutableName) is { } heifEnc
            ? HeifEncImageConverter.Create(heifEnc)
            : MagickImageConverter.SupportsHeicEncoding ? MagickImageConverter.Instance : SkipNoHeicEncoder();

        var merged = await new MotionPhotoMerger(metadata, MagickImageConverter.Instance, videos).MergeAsync(
            new MergeRequest
            {
                Candidates = MediaPairMatcher.Match([photo, video]).Pairs,
                Output = new OutputOptions(temp.Combine("android")),
                SkipValidation = true
            },
            cancellationToken: Token);
        var motionPhoto = Assert.Single(Assert.Single(merged.Items).Outputs);
        Assert.NotNull(MotionPhotoLayout.Locate(motionPhoto));

        var restored = await new MotionPhotoSplitter(metadata, images, videos).SplitAsync(
            new SplitRequest { Files = [motionPhoto], Output = new OutputOptions(temp.Combine("apple")), Target = SplitTarget.Apple },
            cancellationToken: Token);
        var outcome = Assert.Single(restored.Items);
        Assert.True(outcome.Kind == OutcomeKind.Succeeded, outcome.Message);

        var tags = await metadata.ReadAsync(outcome.Outputs, cancellationToken: Token);
        var photoId = tags[outcome.Outputs[0]].ContentIdentifier;
        Assert.False(string.IsNullOrEmpty(photoId));
        Assert.Equal(photoId, tags[outcome.Outputs[1]].ContentIdentifier);
    }

    private static IImageConverter SkipNoHeicEncoder()
    {
        Assert.Skip("没有可用的 HEIC 编码器（heif-enc 或带 HEIC 编码的 Magick.NET）。");
        return null!;
    }

    /// <summary>噪点多的照片经 heif-enc 编码反而比原 JPEG 大：瘦身保留原格式，只剥离视频。</summary>
    [Fact]
    public async Task Strip_WithRealHeifEnc_KeepsJpegWhenHeicIsLarger()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var heifEnc = ExternalTools.RequireHeifEnc();
        using var temp = new TempDirectory();
        byte[] cover;
        using (var image = new MagickImage("gradient:#1E3A8A-#F59E0B", 1200, 900))
        {
            image.AddNoise(NoiseType.Gaussian, 0.5);
            image.Quality = 85;
            cover = image.ToByteArray(MagickFormat.Jpeg);
        }

        var source = temp.CreateFile("in/MVIMG_NOISE.jpg", SyntheticMedia.MotionPhoto(cover, SyntheticMedia.Mp4(40_000)));
        await using var metadata = ExifToolMetadataService.Create(exiftool, maxSessions: 1);

        var report = await new MotionPhotoStripper(metadata, HeifEncImageConverter.Create(heifEnc)).StripAsync(
            new StripRequest { Files = [source], Output = new OutputOptions(temp.Combine("out")), HeicQuality = 90 },
            cancellationToken: Token);

        var outcome = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Succeeded, outcome.Kind);
        Assert.True(outcome.KeptOriginalFormat);
        var output = Assert.Single(outcome.Outputs);
        Assert.Equal(".jpg", Path.GetExtension(output));
        Assert.Null(MotionPhotoLayout.Locate(output));
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length - 40_000 + 4096);
        Assert.Equal(["MVIMG_NOISE.jpg"], temp.FileNames("out"));
    }
}
