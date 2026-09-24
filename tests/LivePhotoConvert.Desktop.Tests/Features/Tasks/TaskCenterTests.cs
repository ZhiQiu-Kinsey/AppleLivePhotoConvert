using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

public class TaskCenterTests
{
    private static ConversionJob ExtractJob(string output = "/out", params string[] files) =>
        Jobs.Files(ConversionAction.Extract, output, files.Length > 0 ? files : ["/in/a.jpg"]);

    [Fact]
    public async Task RunAsync_Success_AddsReportNavigatesRaisesCompletedAndRunsCompletionEffects()
    {
        var runner = ScriptedRunner.Returning(Jobs.Report(ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg")));
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;
        var completed = new List<TaskReportViewModel>();
        center.Completed += (_, r) => completed.Add(r);
        var running = new List<bool>();
        center.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskCenter.IsRunning))
            {
                running.Add(center.IsRunning);
            }
        };

        var report = await center.RunAsync(ExtractJob()).Within();

        Assert.Equal([true, false], running);
        Assert.False(center.IsRunning);
        Assert.Null(center.Current);
        Assert.Same(report, Assert.Single(center.History));
        Assert.Same(report, Assert.Single(completed));
        Assert.Equal(AppPage.Tasks, fixture.Host.Get<INavigator>().Current);
        Assert.Equal(["/out"], fixture.Host.Shell.Requests);
        Assert.True(report.IsCompletedClean);
        Assert.Equal(1, report.SuccessCount);
        Assert.Single(runner.Jobs);
    }

    [Fact]
    public async Task History_IsNewestFirst()
    {
        var runner = ScriptedRunner.Returning(Jobs.Report());
        using var fixture = new TaskCenterFixture(runner);

        var first = await fixture.Center.RunAsync(ExtractJob("/first")).Within();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Strip, "/second", "/in/b.jpg")).Within();

        Assert.Equal([second, first], fixture.Center.History);
        Assert.True(second.FinishedAt > first.FinishedAt);
    }

    [Fact]
    public async Task RunAsync_WhileRunning_RejectsSecondSubmission()
    {
        var release = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner((_, _, _) => release.Task);
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;

        var run = center.RunAsync(ExtractJob());

        Assert.True(center.IsRunning);
        Assert.True(((IBackgroundWork)center).IsBusy);
        Assert.NotNull(center.Current);
        // 同步拒绝：调用方无需等待就能知道提交失败
        Assert.Throws<InvalidOperationException>(() => { _ = center.RunAsync(ExtractJob()); });

        release.SetResult(Jobs.Report());
        await run.Within();
        Assert.False(center.IsRunning);
        Assert.False(((IBackgroundWork)center).IsBusy);
        Assert.Single(runner.Jobs);
        Assert.Single(center.History);
    }

    [Fact]
    public async Task Cancel_ProducesCanceledReportWithoutCompletionEffects()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Jobs.Report();
        });
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;
        var completed = 0;
        center.Completed += (_, _) => completed++;

        var run = center.RunAsync(ExtractJob());
        await started.Task.Within();
        center.Cancel();
        Assert.True(center.Current?.IsCancelling);
        var report = await run.Within();

        Assert.True(report.WasCanceled);
        Assert.False(report.IsFatal);
        Assert.Equal(fixture.Host.Localizer["TaskStatusCanceled"], report.StatusText);
        Assert.Empty(fixture.Host.Shell.Requests);
        Assert.Equal(1, completed);
        Assert.Same(report, Assert.Single(center.History));
    }

    [Fact]
    public async Task Cancel_WhenCoreReturnsPartialReport_KeepsCompletedItems()
    {
        var runner = new ScriptedRunner(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return new BatchReport([ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg")], TimeSpan.FromSeconds(1), Canceled: true);
        });
        using var fixture = new TaskCenterFixture(runner);

        var run = fixture.Center.RunAsync(ExtractJob());
        fixture.Center.Cancel();
        var report = await run.Within();

        Assert.True(report.WasCanceled);
        Assert.Equal(1, report.SuccessCount);
        Assert.Contains(fixture.Host.Localizer["ReportCanceledSuffix"], report.SummaryText);
        Assert.Empty(fixture.Host.Shell.Requests);
    }

    [Fact]
    public async Task TogglePause_BlocksBetweenItemsUntilResumed()
    {
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;
        var runner = new ScriptedRunner(async (_, progress, _) =>
        {
            await allowStart.Task;
            for (var i = 1; i <= 3; i++)
            {
                progress!.Report(new BatchProgress(i, 3, $"{i}.jpg"));
                Interlocked.Increment(ref processed);
            }

            return Jobs.Report();
        });
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;

        var run = center.RunAsync(ExtractJob());
        center.TogglePause();
        Assert.True(center.Current!.IsPaused);
        allowStart.SetResult();

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref processed));
        var running = center.Current!;
        Assert.Equal(1, running.Completed);
        Assert.False(run.IsCompleted);

        // 恢复后工作线程可能立刻跑完并清空 Current，断言持有的卡片而不是再次读取 Current
        center.TogglePause();
        Assert.False(running.IsPaused);
        var report = await run.Within();

        Assert.Equal(3, processed);
        Assert.True(report.IsCompleted);
    }

    [Fact]
    public async Task Cancel_WhilePaused_ReleasesBlockedWorker()
    {
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, progress, _) =>
        {
            await allowStart.Task;
            reported.SetResult();
            // 暂停中的 PausableProgress 在此阻塞，取消后抛出 OperationCanceledException
            progress!.Report(new BatchProgress(1, 2, "1.jpg"));
            return Jobs.Report();
        });
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;

        var run = center.RunAsync(ExtractJob());
        center.TogglePause();
        allowStart.SetResult();
        await reported.Task.Within();
        center.Cancel();
        var report = await run.Within();

        Assert.True(report.WasCanceled);
        Assert.Empty(fixture.Host.Shell.Requests);
    }

    [Fact]
    public async Task CancelAndWaitAsync_StopsWaitingAfterTimeout()
    {
        var release = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 不响应取消的任务：等待必须在超时后结束，不能把关窗流程卡住
        var runner = new ScriptedRunner((_, _, _) => release.Task);
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;
        var run = center.RunAsync(ExtractJob());

        var wait = center.CancelAndWaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(wait.IsCompleted);
        Assert.True(center.Current!.IsCancelling);

        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await wait.Within();
        Assert.True(center.IsRunning);

        release.SetResult(Jobs.Report());
        await run.Within();
        Assert.False(center.IsRunning);
    }

    [Fact]
    public async Task CancelAndWaitAsync_ReturnsWhenTaskEndsBeforeTimeout()
    {
        var runner = new ScriptedRunner(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Jobs.Report();
        });
        using var fixture = new TaskCenterFixture(runner);
        var run = fixture.Center.RunAsync(ExtractJob());

        await fixture.Center.CancelAndWaitAsync(TimeSpan.FromMinutes(5)).Within();

        Assert.True(run.IsCompleted);
        Assert.True((await run).WasCanceled);
    }

    [Fact]
    public async Task CancelAndWaitAsync_WhenIdle_CompletesImmediately()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report()));

        var wait = fixture.Center.CancelAndWaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task RunnerFailure_ProducesFatalReportThatRetriesWholeJob()
    {
        var runner = new ScriptedRunner((_, _, _) => Task.FromException<BatchReport>(new FileNotFoundException("未找到 exiftool")));
        using var fixture = new TaskCenterFixture(runner);
        var job = ExtractJob("/out", "/in/a.jpg", "/in/b.jpg");

        var report = await fixture.Center.RunAsync(job).Within();

        Assert.True(report.IsFatal);
        Assert.False(report.WasCanceled);
        Assert.Equal("未找到 exiftool", report.ErrorMessage);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal("未找到 exiftool", Assert.Single(report.Items).Detail);
        Assert.Same(job, report.RetryJob);
        // 与原行为一致：失败也属于"任务结束"，照常执行完成效果
        Assert.Equal(["/out"], fixture.Host.Shell.Requests);
    }

    [Theory]
    [InlineData(ConversionAction.ToAndroid, "NoMergePairs")]
    [InlineData(ConversionAction.ToApple, "NoSplitCandidates")]
    [InlineData(ConversionAction.Strip, "NoAlbumOrPhotoSelected")]
    public async Task EmptyJob_ReportsReasonWithoutInvokingRunner(ConversionAction action, string messageKey)
    {
        var runner = ScriptedRunner.Returning(Jobs.Report());
        using var fixture = new TaskCenterFixture(runner);

        var report = await fixture.Center.RunAsync(new ConversionJob(action, new ConversionOptions(), new ConversionInputs())).Within();

        Assert.True(report.IsFatal);
        Assert.Equal(fixture.Host.Localizer[messageKey], report.ErrorMessage);
        Assert.Empty(runner.Jobs);
        Assert.False(report.CanRetry);
    }

    [Fact]
    public async Task Progress_UpdatesRunningCardAndNavBadgeSource()
    {
        var allowFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner(async (_, progress, _) =>
        {
            progress!.Report(new BatchProgress(1, 4, "IMG_1.jpg"));
            reported.SetResult();
            await allowFinish.Task;
            return Jobs.Report();
        });
        using var fixture = new TaskCenterFixture(runner);
        var center = fixture.Center;

        var run = center.RunAsync(ExtractJob("/out", "a", "b", "c", "d"));
        await reported.Task.Within();

        Assert.Equal(1, center.Current!.Completed);
        Assert.Equal("25%", center.Current.PercentText);
        Assert.Equal("IMG_1.jpg", center.Current.CurrentFile);

        allowFinish.SetResult();
        await run.Within();
    }

    [Fact]
    public void AppServices_RegistersTaskCenterAsSingleton()
    {
        using var host = new DesktopTestHost();

        Assert.Same(host.Get<TaskCenter>(), host.Get<TasksViewModel>().Center);
        Assert.False(host.Get<TaskCenter>().IsRunning);
    }
}
