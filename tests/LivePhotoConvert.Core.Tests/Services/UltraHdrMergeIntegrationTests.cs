using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ImageMagick;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.UltraHdr;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

/// <summary>
/// 用真实 ExifTool 与 heif-dec 把 iPhone HDR 样片合成为 Ultra HDR 动态照片；缺少工具时跳过。
/// 有 libultrahdr 的 ultrahdr_app 时再用它判定 Ultra HDR 并解码 HDR，与 Apple 公式的真值比较。
/// </summary>
public class UltraHdrMergeIntegrationTests
{
    /// <summary>hdr-sample.heic 的 MakerNote 33 / 48。</summary>
    private const double SampleMaker33 = 1.0255059;
    private const double SampleMaker48 = 0.001685693743;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Media", "UltraHdr", "Fixtures", "hdr-sample.heic");

    [Fact]
    public async Task HdrSample_MergesIntoVerifiedUltraHdrMotionPhoto()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var decoder = ExternalTools.RequireHeifDecoder();
        using var temp = new TempDirectory();
        var (photo, video) = CreatePair(temp, SamplePath);

        var outcome = await MergeAsync(exiftool, decoder, photo, video, temp.Combine("hdr"), preserveHdr: true);
        var sdr = await MergeAsync(exiftool, decoder, photo, video, temp.Combine("sdr"), preserveHdr: false);

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.UltraHdrWritten)], outcome.Notes);
        var output = Assert.Single(outcome.Outputs);
        var layout = MotionPhotoLayout.Inspect(output);
        Assert.True(layout.HasGainMap);
        Assert.NotNull(layout.Video);
        var ultraHdr = UltraHdrJpegWriter.Verify(output);
        Assert.Equal(layout.Video.Offset, ultraHdr.ImageEnd);

        // 切出的视频与源 MP4 逐字节一致
        var bytes = await File.ReadAllBytesAsync(output, Token);
        Assert.Equal(await File.ReadAllBytesAsync(video, Token), bytes.AsSpan((int)layout.Video.Offset, (int)layout.Video.Length).ToArray());

        var tags = await ReadTagsAsync(exiftool, output);
        Assert.Equal("Display P3", tags.GetProperty("ICC_Profile:ProfileDescription").GetString());
        Assert.Equal(2, tags.GetProperty("MPF0:NumberOfImages").GetInt32());
        Assert.Equal(layout.Video.Length, tags.GetProperty("XMP-GCamera:MicroVideoOffset").GetInt64());
        Assert.Equal(1, tags.GetProperty("XMP-GCamera:MotionPhoto").GetInt32());

        var sdrOutput = Assert.Single(sdr.Outputs);
        Assert.Empty(sdr.Notes);
        Assert.False(MotionPhotoLayout.Inspect(sdrOutput).HasGainMap);
        var hdrSize = new FileInfo(output).Length - layout.Video.Length;
        var sdrSize = new FileInfo(sdrOutput).Length - layout.Video.Length;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"封面：SDR {sdrSize} 字节，Ultra HDR {hdrSize} 字节（主图 {ultraHdr.GainMapOffset}，增益图 {ultraHdr.GainMapLength}，{(hdrSize - sdrSize) * 100.0 / sdrSize:+0.0;-0.0}%）");
    }

    [Fact]
    public async Task HdrSample_UltraHdrAppDecodesCloseToAppleFormula()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var decoder = ExternalTools.RequireHeifDecoder();
        var ultraHdrApp = ExternalTools.RequireUltraHdrApp();
        using var temp = new TempDirectory();
        var (photo, video) = CreatePair(temp, SamplePath);
        var output = Assert.Single((await MergeAsync(exiftool, decoder, photo, video, temp.Combine("hdr"), preserveHdr: true)).Outputs);

        var stats = await CompareWithAppleFormulaAsync(ultraHdrApp, decoder, output, SamplePath, AppleHdrHeadroom.Compute(SampleMaker33, SampleMaker48), temp);

        TestContext.Current.TestOutputHelper?.WriteLine(stats.ToString());
        // 增益图采样点上没有插值，误差只来自查表量化与 JPEG 压缩；其它像素还叠加了参考解码器与 Magick 插值方式的差异
        Assert.True(stats.CoSited.Mean < 0.02 && stats.CoSited.P99 < 0.06, stats.ToString());
        Assert.True(stats.All.Mean < 0.03, stats.ToString());
        Assert.True(stats.HighlightRatio > 0.01, "样片应含有超过 SDR 白的高光");
    }

    [Fact]
    public async Task SdrHeic_FallsBackAndRecordsMissingGainMap()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var decoder = ExternalTools.RequireHeifDecoder();
        var heifEnc = ExternalTools.RequireHeifEnc();
        using var temp = new TempDirectory();
        var jpeg = temp.Combine("sdr.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 96, 64))
        {
            image.Write(jpeg, MagickFormat.Jpeg);
        }

        var heic = temp.Combine("sdr.heic");
        Assert.True((await ProcessRunner.RunAsync(heifEnc, ["-q", "80", jpeg, "-o", heic], Token)).Success);
        var (photo, video) = CreatePair(temp, heic);

        var outcome = await MergeAsync(exiftool, decoder, photo, video, temp.Combine("out"), preserveHdr: true);

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.HdrGainMapMissing)], outcome.Notes);
        var output = Assert.Single(outcome.Outputs);
        Assert.False(MotionPhotoLayout.Inspect(output).HasGainMap);
        Assert.Null(UltraHdrJpegWriter.Inspect(output));
    }

    [Fact]
    public async Task UltraHdrMotionPhoto_StripWithRealExifTool_KeepsVerifiedGainMap()
    {
        var exiftool = ExternalTools.RequireExifTool();
        var decoder = ExternalTools.RequireHeifDecoder();
        using var temp = new TempDirectory();
        var (photo, video) = CreatePair(temp, SamplePath);
        var motionPhoto = Assert.Single((await MergeAsync(exiftool, decoder, photo, video, temp.Combine("hdr"), preserveHdr: true)).Outputs);
        var coverLength = MotionPhotoLayout.Inspect(motionPhoto).Video!.ImageEnd;

        await using var metadata = ExifToolMetadataService.Create(exiftool);
        var report = await new MotionPhotoStripper(metadata, MagickImageConverter.Instance).StripAsync(
            new StripRequest { Files = [motionPhoto], Output = new OutputOptions(temp.Combine("slim")), ConvertToHeic = true },
            cancellationToken: Token);

        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.Kind == OutcomeKind.Succeeded, outcome.Detail);
        var slim = Assert.Single(outcome.Outputs);
        Assert.Equal(".jpg", Path.GetExtension(slim));
        var layout = MotionPhotoLayout.Inspect(slim);
        Assert.Null(layout.Video);
        Assert.True(layout.HasGainMap);
        var ultraHdr = UltraHdrJpegWriter.Verify(slim);
        Assert.Equal(new FileInfo(slim).Length, ultraHdr.ImageEnd);
        Assert.InRange(new FileInfo(slim).Length, coverLength - 2048, coverLength);
    }

    /// <summary>
    /// 用 LPC_HDR_SAMPLES 指定的目录中的 HEIC 批量验证并输出体积、耗时与误差；未设置时跳过。
    /// </summary>
    [Fact]
    public async Task ExternalSamples_ReportSizeTimeAndError()
    {
        var directory = Environment.GetEnvironmentVariable("LPC_HDR_SAMPLES");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory), "未设置 LPC_HDR_SAMPLES。");
        var exiftool = ExternalTools.RequireExifTool();
        var decoder = ExternalTools.RequireHeifDecoder();
        var ultraHdrApp = Environment.GetEnvironmentVariable("LPC_ULTRAHDR_APP");
        await using var metadata = ExifToolMetadataService.Create(exiftool);
        foreach (var sample in Directory.EnumerateFiles(directory!, "*.heic").Order(StringComparer.Ordinal))
        {
            using var temp = new TempDirectory();
            var (photo, video) = CreatePair(temp, sample);
            var stopwatch = Stopwatch.StartNew();
            var outcome = await MergeAsync(exiftool, decoder, photo, video, temp.Combine("hdr"), preserveHdr: true);
            var hdrElapsed = stopwatch.Elapsed;
            stopwatch.Restart();
            var sdr = await MergeAsync(exiftool, decoder, photo, video, temp.Combine("sdr"), preserveHdr: false);
            var sdrElapsed = stopwatch.Elapsed;
            var videoLength = new FileInfo(video).Length;
            var hdrSize = new FileInfo(outcome.Outputs[0]).Length - videoLength;
            var sdrSize = new FileInfo(sdr.Outputs[0]).Length - videoLength;
            var line = $"{Path.GetFileName(sample)}：源 {new FileInfo(sample).Length} 字节，SDR 封面 {sdrSize}，HDR 封面 {hdrSize}（{(hdrSize - sdrSize) * 100.0 / sdrSize:+0.0;-0.0}%，其中增益图 {UltraHdrJpegWriter.Inspect(outcome.Outputs[0])?.GainMapLength ?? 0}），"
                       + $"耗时 SDR {sdrElapsed.TotalMilliseconds:F0} ms / HDR {hdrElapsed.TotalMilliseconds:F0} ms，附注 {string.Join(",", outcome.Notes.Select(note => note.Kind))}";
            var tags = (await metadata.ReadAsync([sample], cancellationToken: Token))[sample];
            if (outcome.Notes is [{ Kind: OutcomeNoteKind.UltraHdrWritten }] && !string.IsNullOrWhiteSpace(ultraHdrApp) && tags is { AppleHdrHeadroom: { } m33, AppleHdrGain: { } m48 })
            {
                line += "；" + await CompareWithAppleFormulaAsync(ultraHdrApp, decoder, outcome.Outputs[0], sample, AppleHdrHeadroom.Compute(m33, m48), temp);
            }

            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }
    }

    private static (string Photo, string Video) CreatePair(TempDirectory temp, string heic)
    {
        var photo = temp.Combine("IMG_0001.HEIC");
        File.Copy(heic, photo);
        var video = temp.CreateFile("IMG_0001.MP4", SyntheticMedia.Mp4(64 * 1024));
        return (photo, video);
    }

    private static async Task<ItemOutcome> MergeAsync(string exiftool, HeifDecoder decoder, string photo, string video, string output, bool preserveHdr)
    {
        await using var metadata = ExifToolMetadataService.Create(exiftool);
        var report = await new MotionPhotoMerger(metadata, MagickImageConverter.Instance, new FakeVideoConverter(), decoder).MergeAsync(
            new MergeRequest
            {
                Candidates = MediaPairMatcher.Match([photo, video]).Pairs,
                Output = new OutputOptions(output),
                SkipValidation = true,
                PreserveHdr = preserveHdr
            },
            cancellationToken: Token);
        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.Kind == OutcomeKind.Succeeded, outcome.Detail);
        return outcome;
    }

    private static async Task<JsonElement> ReadTagsAsync(string exiftool, string path)
    {
        var result = await ProcessRunner.RunAsync(exiftool, ["-j", "-n", "-G1", "-a", "-ICC_Profile:ProfileDescription", "-MPF:NumberOfImages", "-XMP-GCamera:all", path], Token);
        Assert.True(result.Success, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement[0].Clone();
    }

    /// <summary>
    /// ultrahdr_app 以线性半精度输出 HDR（SDR 白 = 1），与「SDR 线性 × Apple 增益」的真值按 Display P3 亮度比较，误差以档（log2）计。
    /// 分两组统计：增益图采样点对应的像素（无插值）与全部像素（Apple 增益图用双线性放大）。
    /// </summary>
    private static async Task<ErrorStats> CompareWithAppleFormulaAsync(string ultraHdrApp, HeifDecoder decoder, string motionPhoto, string heic, double headroom, TempDirectory temp)
    {
        var probe = await ProcessRunner.RunAsync(ultraHdrApp, ["-m", "1", "-j", motionPhoto, "-P"], Token);
        Assert.True(probe.Success && probe.StandardOutput.Contains("Ultra HDR Image: Yes", StringComparison.Ordinal), probe.StandardOutput + probe.StandardError);

        var raw = temp.Combine("hdr.raw");
        var decoded = await ProcessRunner.RunAsync(ultraHdrApp, ["-m", "1", "-j", motionPhoto, "-o", "0", "-O", "4", "-z", raw], Token);
        Assert.True(decoded.Success, decoded.StandardOutput + decoded.StandardError);

        var apple = await decoder.DecodeAsync(heic, temp.Combine("apple"), Token);
        Assert.NotNull(apple);
        using var primary = new MagickImage(motionPhoto);
        var width = (int)primary.Width;
        var height = (int)primary.Height;
        primary.Depth = 8;
        var sdr = primary.ToByteArray(MagickFormat.Rgb);
        using var gainMapImage = new MagickImage(apple.GainMapPath);
        var gainMapWidth = (int)gainMapImage.Width;
        var scale = width / gainMapWidth;
        gainMapImage.Depth = 8;
        var nativeGain = gainMapImage.ToByteArray(MagickFormat.Gray);
        gainMapImage.FilterType = FilterType.Triangle;
        gainMapImage.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        var gain = gainMapImage.ToByteArray(MagickFormat.Gray);

        var hdr = await File.ReadAllBytesAsync(raw, Token);
        Assert.Equal((long)width * height * 8, hdr.Length);
        var all = new List<double>();
        var coSited = new List<double>();
        long highlights = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var yHdr = Luminance(Half(hdr, i * 8), Half(hdr, i * 8 + 2), Half(hdr, i * 8 + 4));
                var ySdr = Luminance(Srgb(sdr[i * 3]), Srgb(sdr[i * 3 + 1]), Srgb(sdr[i * 3 + 2]));
                var yRef = ySdr * AppleBoost(headroom, gain[i]);
                highlights += yRef > 1 ? 1 : 0;
                if (yRef > 0.01 && yHdr > 0.01)
                {
                    all.Add(Math.Abs(Math.Log2(yHdr / yRef)));
                }

                if (scale > 0 && x % scale == 0 && y % scale == 0 && x / scale < gainMapWidth && ySdr * AppleBoost(headroom, nativeGain[y / scale * gainMapWidth + x / scale]) is var yNative && yNative > 0.01 && yHdr > 0.01)
                {
                    coSited.Add(Math.Abs(Math.Log2(yHdr / yNative)));
                }
            }
        }

        return new ErrorStats(headroom, Summarize(coSited), Summarize(all), (double)highlights / ((long)width * height));
    }

    private static double AppleBoost(double headroom, byte gain) => 1 + (headroom - 1) * AppleGainMapConverter.Rec709InverseOetf(gain / 255.0);

    private static (double Mean, double P99) Summarize(List<double> errors)
    {
        errors.Sort();
        return (errors.Average(), errors[(int)(errors.Count * 0.99)]);
    }

    private static double Half(byte[] bytes, int offset) => (double)BitConverter.ToHalf(bytes, offset);

    private static double Srgb(byte value)
    {
        var v = value / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(double r, double g, double b) => 0.2289746 * r + 0.6917385 * g + 0.0792869 * b;

    private sealed record ErrorStats(double Headroom, (double Mean, double P99) CoSited, (double Mean, double P99) All, double HighlightRatio)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"H={Headroom:F3}，与 Apple 公式真值的亮度误差：增益图采样点 平均 {CoSited.Mean:F4} / p99 {CoSited.P99:F4} 档，全部像素 平均 {All.Mean:F4} / p99 {All.P99:F4} 档，超过 SDR 白的像素 {HighlightRatio * 100:F2}%");
    }
}
