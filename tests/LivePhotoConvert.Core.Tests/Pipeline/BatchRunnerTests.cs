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
        Assert.Equal("坏文件", Assert.Single(report.Items, item => item.Kind == OutcomeKind.Failed).Message);
        Assert.False(report.Canceled);
    }

    [Fact]
    public async Task RunAsync_ResolvedOutcomes_CountTowardsTotalAndProgress()
    {
        var progress = new List<BatchProgress>();

        var report = await BatchRunner.RunAsync(
            ["a"],
            item => item,
            (item, _) => Task.FromResult(ItemOutcome.Succeeded(item)),
            1,
            new SynchronousProgress(progress.Add),
            [ItemOutcome.Skipped("x", "校验未通过")],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Items.Count);
        Assert.Equal(new BatchProgress(2, 2, "a"), Assert.Single(progress));
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
