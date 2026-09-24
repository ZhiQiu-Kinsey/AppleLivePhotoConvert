using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Tests.Pipeline;

public class BatchRunnerTests
{
    [Fact]
    public async Task RunAsync_ItemException_IsRecordedAsFailureWithoutStoppingBatch()
    {
        var report = await BatchRunner.RunAsync(
            ["a", "b", "c"],
            item => item,
            (item, _) => item == "b" ? throw new InvalidDataException("坏文件") : Task.FromResult(ItemOutcome.Succeeded(item)),
            parallelism: 2,
            progress: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Succeeded);
        var failed = Assert.Single(report.Items, item => item.Kind == OutcomeKind.Failed);
        Assert.Equal(OutcomeReason.Unexpected, failed.Reason);
        Assert.Equal("坏文件", failed.Detail);
        Assert.False(report.Canceled);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("encode")]
    [InlineData("hdr")]
    [InlineData("verify")]
    [InlineData("other")]
    public async Task RunAsync_ItemException_IsMappedToReasonAndKeepsMessageAsDetail(string kind)
    {
        (Exception exception, OutcomeCause expected) = kind switch
        {
            "tool" => (new ToolNotFoundException("ffmpeg"), new OutcomeCause(OutcomeReason.ToolMissing, "ffmpeg")),
            "encode" => (new VideoConversionException(VideoConversionError.EncodeFailed, "重新编码失败"), OutcomeReason.VideoConversionFailed),
            "hdr" => (new VideoConversionException(VideoConversionError.HdrEncoderUnavailable, "缺少 libx265"), OutcomeReason.HdrEncoderUnavailable),
            "verify" => (new OutcomeException(OutcomeReason.VerificationFailed, "校验失败"), OutcomeReason.VerificationFailed),
            _ => ((Exception)new IOException("磁盘已满"), (OutcomeCause)OutcomeReason.Unexpected)
        };

        var report = await BatchRunner.RunAsync(
            ["a"],
            item => item,
            (_, _) => Task.FromException<ItemOutcome>(exception),
            parallelism: 1,
            progress: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(report.Items);
        Assert.Equal(OutcomeKind.Failed, failed.Kind);
        Assert.Equal(expected, Assert.Single(failed.Causes));
        Assert.Equal(exception.Message, failed.Detail);
    }

    [Fact]
    public async Task RunAsync_ResolvedOutcomes_CountTowardsTotalAndProgress_AndAreReportedSeparately()
    {
        var progress = new List<BatchProgress>();

        var report = await BatchRunner.RunAsync(
            ["a"],
            item => item,
            (item, _) => Task.FromResult(ItemOutcome.Succeeded(item)),
            1,
            new SynchronousProgress(progress.Add),
            [ItemOutcome.Skipped("x", OutcomeReason.PairCaptureTimeVideoOnly)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);
        // 预先确定的条目计入已完成，同时单独给出，界面据此只按实际处理的条目计算吞吐
        Assert.Equal(new BatchProgress(2, 2, "a", Preresolved: 1), Assert.Single(progress));
    }

    [Fact]
    public async Task RunAsync_Canceled_ReturnsPartialReport()
    {
        using var cts = new CancellationTokenSource();
        var report = await BatchRunner.RunAsync(
            Enumerable.Range(0, 100).Select(i => i.ToString()).ToList(),
            item => item,
            async (item, token) =>
            {
                if (item == "3")
                {
                    await cts.CancelAsync();
                }

                await Task.Delay(1, token);
                return ItemOutcome.Succeeded(item);
            },
            1,
            null,
            cancellationToken: cts.Token);

        Assert.True(report.Canceled);
        Assert.InRange(report.Items.Count, 1, 4);
    }

    private sealed class SynchronousProgress(Action<BatchProgress> report) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => report(value);
    }
}
