using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Media.UltraHdr;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests.Services;

/// <summary>
/// 合成时保留 iPhone HDR 增益图：成功组装 Ultra HDR，或按原因降级为 SDR。
/// </summary>
public class MotionPhotoMergerHdrTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeMetadataService _metadata = new();
    private readonly FakeImageConverter _images = new();
    private readonly FakeVideoConverter _videos = new();

    private async Task<(ItemOutcome Outcome, string Photo)> MergeAsync(
        TempDirectory temp,
        FakeGainMapDecoder? decoder,
        MediaMetadata? photoTags = null,
        byte[]? heic = null,
        bool preserveHdr = true)
    {
        var photo = temp.CreateFile("IMG_0001.HEIC", heic ?? SyntheticMedia.Heic());
        var video = temp.CreateFile("IMG_0001.MP4", SyntheticMedia.Mp4(5000));
        if (photoTags is not null)
        {
            _metadata.Set(photo, photoTags with { Path = photo });
        }

        var report = await new MotionPhotoMerger(_metadata, _images, _videos, decoder).MergeAsync(
            new MergeRequest
            {
                Candidates = MediaPairMatcher.Match([photo, video]).Pairs,
                Output = new OutputOptions(temp.Combine("out")),
                PreserveHdr = preserveHdr
            },
            cancellationToken: Token);
        var outcome = Assert.Single(report.Items);
        Assert.True(outcome.Kind == OutcomeKind.Succeeded, outcome.Message);
        return (outcome, photo);
    }

    private static MediaMetadata HdrTags(double? maker33 = 1.0255059, double? maker48 = 0.001685693743) => new()
    {
        Path = string.Empty,
        HasAppleGainMap = true,
        HdrGainMapVersion = 65536,
        AppleHdrHeadroom = maker33,
        AppleHdrGain = maker48
    };

    [Fact]
    public async Task HeicWithGainMap_ProducesUltraHdrMotionPhoto()
    {
        using var temp = new TempDirectory();
        var decoder = new FakeGainMapDecoder();

        var (outcome, photo) = await MergeAsync(temp, decoder, HdrTags());

        var output = Assert.Single(outcome.Outputs);
        Assert.Equal([new OutcomeNote(OutcomeNoteKind.UltraHdrWritten)], outcome.Notes);
        Assert.Equal([photo], decoder.Decoded);
        Assert.Empty(_images.JpegConversions);
        Assert.Equal([photo], _metadata.CoverCopies);

        var layout = MotionPhotoLayout.Inspect(output);
        Assert.True(layout.HasGainMap);
        Assert.Equal(5000, layout.Video?.Length);
        var ultraHdr = UltraHdrJpegWriter.Inspect(output)!;
        Assert.True(ultraHdr.IsPrimaryLengthConsistent);
        Assert.Equal(layout.Video!.Offset, ultraHdr.ImageEnd);
        Assert.Equal(ultraHdr.GainMapLength, ultraHdr.DirectoryGainMapLength);

        await using var stream = File.OpenRead(output);
        var items = MotionPhotoXmp.Parse(MotionPhotoLayout.ReadJpegXmp(stream))!.Items;
        Assert.Equal(["Primary", "GainMap", "MotionPhoto"], items.Select(item => item.Semantic));
    }

    [Fact]
    public async Task HeicWithGainMap_WithoutDecoder_FallsBackToSdr()
    {
        using var temp = new TempDirectory();

        var (outcome, photo) = await MergeAsync(temp, decoder: null, HdrTags());

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.HdrDecoderUnavailable)], outcome.Notes);
        AssertSdrMotionPhoto(Assert.Single(outcome.Outputs));
        Assert.Equal([photo], _images.JpegConversions);
    }

    [Fact]
    public async Task HeicWithoutGainMap_RecordsMissingGainMap()
    {
        using var temp = new TempDirectory();
        var decoder = new FakeGainMapDecoder();

        var (outcome, _) = await MergeAsync(temp, decoder, new MediaMetadata { Path = string.Empty, AppleHdrHeadroom = 0 });

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.HdrGainMapMissing)], outcome.Notes);
        Assert.Empty(decoder.Decoded);
        AssertSdrMotionPhoto(Assert.Single(outcome.Outputs));
    }

    [Fact]
    public async Task HeicWithOnlyToneMapItem_RecordsToneMapNotSupported()
    {
        using var temp = new TempDirectory();

        var (outcome, _) = await MergeAsync(temp, new FakeGainMapDecoder(), heic: SyntheticMedia.HeifWithItems("hvc1", "hvc1", "tmap"));

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.HdrToneMapNotSupported)], outcome.Notes);
    }

    [Fact]
    public async Task DecoderFindsNoGainMap_RecordsMissingGainMap()
    {
        using var temp = new TempDirectory();

        var (outcome, photo) = await MergeAsync(temp, new FakeGainMapDecoder { ReturnNull = true }, HdrTags());

        Assert.Equal([new OutcomeNote(OutcomeNoteKind.HdrGainMapMissing)], outcome.Notes);
        Assert.Equal([photo], _images.JpegConversions);
    }

    [Fact]
    public async Task DecoderFails_FallsBackToSdrWithReason()
    {
        using var temp = new TempDirectory();
        var decoder = new FakeGainMapDecoder { Failure = new InvalidOperationException("heif-dec 解码失败：boom") };

        var (outcome, _) = await MergeAsync(temp, decoder, HdrTags());

        var note = Assert.Single(outcome.Notes);
        Assert.Equal(OutcomeNoteKind.HdrConversionFailed, note.Kind);
        Assert.Contains("boom", note.Detail, StringComparison.Ordinal);
        AssertSdrMotionPhoto(Assert.Single(outcome.Outputs));
    }

    [Theory]
    [InlineData(null, 0.001)]
    [InlineData(1.2, null)]
    [InlineData(1.2, 10.0)] // 余量为 1，增益图不产生效果
    public async Task MissingOrUselessHeadroom_SkipsDecoding(double? maker33, double? maker48)
    {
        using var temp = new TempDirectory();
        var decoder = new FakeGainMapDecoder();

        var (outcome, _) = await MergeAsync(temp, decoder, HdrTags(maker33, maker48));

        Assert.Equal(OutcomeNoteKind.HdrMetadataMissing, Assert.Single(outcome.Notes).Kind);
        Assert.Empty(decoder.Decoded);
    }

    [Fact]
    public async Task PreserveHdrDisabled_NeverDecodesAndRecordsNothing()
    {
        using var temp = new TempDirectory();
        var decoder = new FakeGainMapDecoder();

        var (outcome, _) = await MergeAsync(temp, decoder, HdrTags(), preserveHdr: false);

        Assert.Empty(outcome.Notes);
        Assert.Empty(decoder.Decoded);
        AssertSdrMotionPhoto(Assert.Single(outcome.Outputs));
    }

    [Fact]
    public async Task WritingMotionPhotoToUltraHdrCoverFails_RetriesWithSdrCover()
    {
        using var temp = new TempDirectory();
        var calls = 0;
        // 第 1 次写入是复制封面元数据，第 2 次是在 Ultra HDR 封面上写动态照片声明
        _metadata.FailWrites = _ => Interlocked.Increment(ref calls) == 2;

        var (outcome, _) = await MergeAsync(temp, new FakeGainMapDecoder(), HdrTags());

        Assert.Equal(OutcomeNoteKind.HdrConversionFailed, Assert.Single(outcome.Notes).Kind);
        AssertSdrMotionPhoto(Assert.Single(outcome.Outputs));
        Assert.Single(_metadata.MotionPhotoWrites);
    }

    [Fact]
    public async Task JpegCover_IsNotTouchedByHdrPath()
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("IMG_0001.JPG", SyntheticMedia.Jpeg());
        var video = temp.CreateFile("IMG_0001.MOV", SyntheticMedia.Mov());
        var decoder = new FakeGainMapDecoder();

        var report = await new MotionPhotoMerger(_metadata, _images, _videos, decoder).MergeAsync(
            new MergeRequest { Candidates = MediaPairMatcher.Match([photo, video]).Pairs, Output = new OutputOptions(temp.Combine("out")) },
            cancellationToken: Token);

        Assert.Empty(Assert.Single(report.Items).Notes);
        Assert.Empty(decoder.Decoded);
    }

    private static void AssertSdrMotionPhoto(string output)
    {
        var layout = MotionPhotoLayout.Inspect(output);
        Assert.False(layout.HasGainMap);
        Assert.Equal(5000, layout.Video?.Length);
        Assert.Null(UltraHdrJpegWriter.Inspect(output));
    }
}
