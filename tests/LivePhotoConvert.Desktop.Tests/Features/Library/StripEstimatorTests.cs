using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Tests.Features.Dialogs;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.Time.Testing;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>相册级瘦身预估：视频字节精确、HEIC 体积按抽样实测压缩比（像素加权）外推、样张结果缓存与失效、ExifTool 会话复用。</summary>
public sealed class StripEstimatorTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();
    private readonly CountingEngines _engines = new(new LossyStandInEncoder());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task HeicSize_IsExtrapolatedFromPixelWeightedSampleRatios()
    {
        var small = Photo("MVIMG_S.jpg", 400, 300);
        var large = Photo("MVIMG_L.jpg", 800, 600);
        var photoBytes = await PhotoBytesAsync(small, large);
        var sampler = new ScriptedSampler(path => (long)(photoBytes[path] * (path == small ? 0.2 : 0.6)));
        using var estimator = Create(sampler);

        var estimate = await estimator.EstimateAsync([small, large], ToolPaths.Auto, convertToHeic: true, heicQuality: 90, Token);

        var smallRatio = (double)(long)(photoBytes[small] * 0.2) / photoBytes[small];
        var largeRatio = (double)(long)(photoBytes[large] * 0.6) / photoBytes[large];
        var expected = (smallRatio * 400 * 300 + largeRatio * 800 * 600) / (400 * 300 + 800 * 600);
        Assert.Equal(2, estimate.Count);
        Assert.Equal(2, estimate.SampledCount);
        Assert.Equal(expected, estimate.HeicSizeRatio!.Value, 9);
        Assert.Equal((long)(photoBytes[small] * expected) + (long)(photoBytes[large] * expected), estimate.EstimatedBytes);
        Assert.Equal(new FileInfo(small).Length + new FileInfo(large).Length, estimate.OriginalBytes);
        Assert.All(sampler.Requests, r => Assert.Equal(new StripSampleOptions(ToolPaths.Auto, true, 90), r.Options));
    }

    [Fact]
    public async Task WithoutConversion_VideoBytesAreExactAndNothingIsSampled()
    {
        var photo = Photo("MVIMG_1.jpg", 320, 240, videoBytes: 70_000);
        var sampler = new ScriptedSampler(_ => 1);
        using var estimator = Create(sampler);

        var estimate = await estimator.EstimateAsync([photo], ToolPaths.Auto, convertToHeic: false, heicQuality: 90, Token);

        Assert.Equal(0, sampler.Calls);
        Assert.Equal(0, estimate.SampledCount);
        Assert.Null(estimate.HeicSizeRatio);
        Assert.Equal((await PhotoBytesAsync(photo))[photo], estimate.EstimatedBytes);
        Assert.True(estimate.SavedBytes >= 70_000);
    }

    [Fact]
    public async Task SampleResults_AreCachedByPathSizeTimeAndQuality()
    {
        string[] photos = [Photo("MVIMG_1.jpg", 320, 240), Photo("MVIMG_2.jpg", 320, 240, seed: 1), Photo("MVIMG_3.jpg", 320, 240, seed: 2)];
        var sampler = new ScriptedSampler(path => new FileInfo(path).Length / 4);
        using var estimator = Create(sampler);

        var first = await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 90, Token);
        Assert.Equal(3, sampler.Calls);

        var again = await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 90, Token);
        Assert.Equal(3, sampler.Calls);
        Assert.Equal(first, again);

        // 修改时间变化：只有这一张重新抽样
        File.SetLastWriteTimeUtc(photos[1], File.GetLastWriteTimeUtc(photos[1]).AddMinutes(1));
        await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 90, Token);
        Assert.Equal(4, sampler.Calls);
        Assert.Equal(photos[1], sampler.Requests[^1].Photo);

        // 质量变化：全部重新抽样
        await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 70, Token);
        Assert.Equal(7, sampler.Calls);
        Assert.All(sampler.Requests.Skip(4), r => Assert.Equal(70, r.Options.HeicQuality));

        // 大小变化（修改时间相同也算新文件）
        var time = File.GetLastWriteTimeUtc(photos[2]);
        Photo("MVIMG_3.jpg", 320, 240, videoBytes: 41_000, seed: 2);
        File.SetLastWriteTimeUtc(photos[2], time);
        await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 70, Token);
        Assert.Equal(8, sampler.Calls);
    }

    [Fact]
    public async Task RealSampler_ReusesOneExifToolSessionUntilIdle()
    {
        string[] photos = [Photo("MVIMG_1.jpg", 320, 240), Photo("MVIMG_2.jpg", 320, 240, seed: 1)];
        var time = new FakeTimeProvider();
        var estimator = new StripEstimator(_engines, new MetadataSessionPool(_engines, time), sampler: null);

        var estimate = await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 90, Token);
        await estimator.EstimateAsync(photos, ToolPaths.Auto, false, 90, Token);

        // 分析与两张样张的处理共用同一个会话
        Assert.Equal(2, estimate.SampledCount);
        Assert.Equal(1, _engines.MetadataCreated);
        Assert.Equal(0, _engines.MetadataDisposed);

        time.Advance(MetadataSessionPool.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _engines.MetadataDisposed);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _engines.MetadataDisposed);

        await estimator.EstimateAsync(photos, ToolPaths.Auto, true, 90, Token);
        Assert.Equal(2, _engines.MetadataCreated);

        // 工具路径变化：换新会话，旧会话立即释放
        await estimator.EstimateAsync(photos, new ToolPaths("/custom/exiftool", null, null), false, 90, Token);
        Assert.Equal(3, _engines.MetadataCreated);
        Assert.Equal(2, _engines.MetadataDisposed);

        estimator.Dispose();
        Assert.Equal(3, _engines.MetadataDisposed);
    }

    [Fact]
    public async Task SampleFailures_FallBackToDefaultRatio_ButMissingEncoderIsReported()
    {
        var photo = Photo("MVIMG_1.jpg", 320, 240);
        using var failing = Create(new ScriptedSampler(_ => 0) { Failure = new InvalidOperationException("坏样张") });

        var estimate = await failing.EstimateAsync([photo], ToolPaths.Auto, true, 90, Token);

        Assert.Equal(0, estimate.SampledCount);
        Assert.Equal(StripCandidate.DefaultHeicSizeRatio, estimate.HeicSizeRatio);

        using var missing = Create(new ScriptedSampler(_ => 0) { Failure = new ToolNotFoundException("heif-enc") });
        await Assert.ThrowsAsync<ToolNotFoundException>(() => missing.EstimateAsync([photo], ToolPaths.Auto, true, 90, Token));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(40, 3)]
    [InlineData(100, 5)]
    [InlineData(5000, 5)]
    public void SampleCount_IsThreeToFive(int candidates, int expected) =>
        Assert.Equal(expected, StripEstimator.SampleCountFor(candidates));

    [Fact]
    public void PickSamples_DoesNotDependOnOrder_AndMostlySurvivesSmallChanges()
    {
        var candidates = Enumerable.Range(0, 60).Select(i => Candidate($"/album/IMG_{i:D4}.jpg")).ToList();

        var picked = StripEstimator.PickSamples(candidates).Select(c => c.ImagePath).ToList();
        var reversed = StripEstimator.PickSamples([.. Enumerable.Reverse(candidates)]).Select(c => c.ImagePath).ToList();
        var grown = StripEstimator.PickSamples([.. candidates, Candidate("/album/IMG_9999.jpg")]).Select(c => c.ImagePath).ToList();

        Assert.Equal(3, picked.Count);
        Assert.Equal(picked, reversed);
        Assert.True(picked.Intersect(grown).Count() >= 2);
    }

    private static StripCandidate Candidate(string path) => new(path, 1000, null, null, 0, false);

    private StripEstimator Create(IStripSampler sampler) =>
        new(_engines, new MetadataSessionPool(_engines, TimeProvider.System), sampler);

    private string Photo(string name, int width, int height, int videoBytes = 40_000, int seed = 0) =>
        CompareSamples.WriteMotionPhoto(Path.Combine(_sandbox.InputDirectory, name), width, height, videoBytes, seed);

    private async Task<Dictionary<string, long>> PhotoBytesAsync(params string[] photos)
    {
        var candidates = await new MotionPhotoStripper(_engines.Metadata, new LossyStandInEncoder()).AnalyzeAsync(photos, Token);
        return candidates.ToDictionary(c => c.ImagePath, c => c.PhotoBytes);
    }

    /// <summary>按脚本给出产物大小的样张处理器，不写任何文件。</summary>
    private sealed class ScriptedSampler(Func<string, long> productBytes) : IStripSampler
    {
        public List<(string Photo, StripSampleOptions Options)> Requests { get; } = [];

        public int Calls => Requests.Count;

        public Exception? Failure { get; init; }

        public Task<StripSample> SampleAsync(string photoPath, StripSampleOptions options, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((photoPath, options));
            }

            return Failure is { } failure
                ? Task.FromException<StripSample>(failure)
                : Task.FromResult(new StripSample(photoPath, photoPath, 0, productBytes(photoPath), Converted: true, "scripted"));
        }
    }
}
