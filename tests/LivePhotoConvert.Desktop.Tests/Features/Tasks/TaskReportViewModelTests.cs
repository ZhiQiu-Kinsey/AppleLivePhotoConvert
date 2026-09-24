using System.Text;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Features.Dialogs;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

public class TaskReportViewModelTests
{
    private static readonly BatchReport MixedReport = new(
    [
        ItemOutcome.Succeeded("/in/ok1.jpg", "/out/ok1.jpg"),
        ItemOutcome.Skipped("/in/skip.jpg", OutcomeReason.NotMotionPhoto),
        ItemOutcome.Failed("/in/bad1.jpg", OutcomeReason.Unexpected, "写入失败"),
        ItemOutcome.Failed("/in/bad2.jpg", new OutcomeCause(OutcomeReason.ToolMissing, "ffmpeg"), "未找到 ffmpeg"),
        ItemOutcome.Succeeded("/in/ok2.jpg", "/out/ok2.jpg") with { CleanupError = "ok2.jpg: Access denied" }
    ], TimeSpan.FromSeconds(10), Canceled: false);

    private static ConversionJob SplitJob() => Jobs.Files(
        ConversionAction.ToApple,
        "/out",
        "/in/ok1.jpg", "/in/skip.jpg", "/in/bad1.jpg", "/in/bad2.jpg", "/in/ok2.jpg") with { Parallelism = 2 };

    [Fact]
    public async Task Counts_OrderAndFilters()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var report = await fixture.Center.RunAsync(SplitJob()).Within();

        Assert.Equal(5, report.TotalCount);
        Assert.Equal(2, report.SuccessCount);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(1, report.CleanupCount);
        Assert.Equal(3, report.ProblemCount);
        Assert.True(report.IsCompletedWithProblems);
        Assert.Equal("40.0%", report.SuccessPercentText);
        Assert.Equal(fixture.Host.Localizer.Format("ReportSummaryFormat", 2, 3, 1), report.SummaryText);

        // 失败项在前，清理失败紧跟对应的成功项
        Assert.Equal(
            [TaskItemStatus.Failed, TaskItemStatus.Failed, TaskItemStatus.Skipped, TaskItemStatus.Success, TaskItemStatus.Success, TaskItemStatus.Cleanup],
            report.Items.Select(i => i.Status));
        Assert.Equal(6, report.FilteredItems.Count);

        report.SetFilterCommand.Execute("1");
        Assert.True(report.IsProblemFilterSelected);
        Assert.Equal(4, report.FilteredItems.Count);
        Assert.DoesNotContain(report.FilteredItems, i => i.IsSuccess);

        report.SetFilterCommand.Execute(2);
        Assert.True(report.IsSuccessFilterSelected);
        Assert.Equal(["ok1.jpg", "ok2.jpg"], report.FilteredItems.Select(i => i.FileName));

        report.SetFilterCommand.Execute("0");
        Assert.True(report.IsAllFilterSelected);
        Assert.Equal(6, report.FilteredItems.Count);
    }

    [Fact]
    public async Task Items_CarryLocalizedStatusFormatsAndLocatePaths()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var localizer = fixture.Host.Localizer;
        var report = await fixture.Center.RunAsync(SplitJob()).Within();

        var ok = report.Items.First(i => i.IsSuccess);
        Assert.Equal(localizer["ReportStatusSuccess"], ok.StatusText);
        Assert.Equal("JPG", ok.SourceFormat);
        Assert.Equal(localizer["SplitTargetApple"], ok.TargetFormat);
        Assert.Equal(localizer.Format("SplitSuccessDescFormat", localizer["SplitTargetApple"]), ok.Detail);
        Assert.Equal("/out/ok1.jpg", ok.LocatePath);

        var bad = report.Items.First(i => i.IsFailed);
        Assert.Equal(localizer["OutcomeReasonUnexpected"], bad.Detail);
        Assert.Equal("写入失败", bad.TechnicalDetail);
        Assert.True(bad.HasTechnicalDetail);
        Assert.Equal("/in/bad1.jpg", bad.LocatePath);

        var missingTool = report.Items.Single(i => i.SourcePath == "/in/bad2.jpg");
        Assert.Equal(localizer.Format("ToolMissingFormat", "ffmpeg"), missingTool.Detail);
        Assert.Equal("未找到 ffmpeg", missingTool.TechnicalDetail);

        Assert.Equal(localizer["OutcomeReasonNotMotionPhoto"], report.Items.Single(i => i.Status == TaskItemStatus.Skipped).Detail);

        var cleanup = report.Items.Single(i => i.Status == TaskItemStatus.Cleanup);
        Assert.Equal(localizer["ReportStatusCleanup"], cleanup.StatusText);
        Assert.Equal(localizer["ReportCleanupFailedDesc"], cleanup.Detail);
        Assert.Equal("ok2.jpg: Access denied", cleanup.TechnicalDetail);
        Assert.False(ok.HasTechnicalDetail);
        Assert.False(ok.HasExtras);
    }

    [Fact]
    public async Task RevealItem_OpensOutputForSuccessAndSourceForFailure()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var report = await fixture.Center.RunAsync(SplitJob()).Within();
        fixture.Host.Shell.Requests.Clear();

        await report.RevealItemCommand.ExecuteAsync(report.Items.First(i => i.IsSuccess));
        await report.RevealItemCommand.ExecuteAsync(report.Items.First(i => i.IsFailed));

        Assert.Equal(["/out/ok1.jpg", "/in/bad1.jpg"], fixture.Host.Shell.Requests);
    }

    [Fact]
    public async Task ShellFailure_IsReportedThroughDialog()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var report = await fixture.Center.RunAsync(SplitJob()).Within();
        fixture.Host.Shell.Result = false;

        var open = report.OpenOutputDirCommand.ExecuteAsync(null);

        var alert = Assert.IsType<ConfirmDialogViewModel>(fixture.Host.Get<IDialogService>().Current);
        Assert.True(alert.IsSingleButton);
        Assert.Contains("/out", alert.Message);
        alert.ConfirmCommand.Execute(null);
        await open.Within();
    }

    [Fact]
    public async Task RetryJob_KeepsActionAndOptionsButOnlyFailedSources()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var job = SplitJob();
        var report = await fixture.Center.RunAsync(job).Within();

        Assert.True(report.CanRetry);
        Assert.Equal(2, report.RetryCount);
        Assert.Equal(ConversionAction.ToApple, report.RetryJob.Action);
        Assert.Same(job.Options, report.RetryJob.Options);
        Assert.Equal(job.Parallelism, report.RetryJob.Parallelism);
        Assert.Equal(["/in/bad1.jpg", "/in/bad2.jpg"], report.RetryJob.Inputs.Files);
        Assert.Equal(fixture.Host.Localizer.Format("RetryFailedFormat", 2), report.RetryText);
    }

    [Fact]
    public async Task RetryJob_ForMerge_FiltersPairsAndForcedPairsByPhoto()
    {
        var good = new MediaPair("/in/a.jpg", "/in/a.mov");
        var bad = new MediaPair("/in/b.jpg", "/in/b.mov");
        var forcedBad = new MediaPair("/in/c.jpg", "/in/c.mov");
        var runner = ScriptedRunner.Returning(Jobs.Report(
            ItemOutcome.Succeeded(good.PhotoPath, "/out/MVIMG_a.jpg"),
            ItemOutcome.Failed(bad.PhotoPath, OutcomeReason.Unexpected, "x"),
            ItemOutcome.Failed(forcedBad.PhotoPath, OutcomeReason.Unexpected, "y")));
        using var fixture = new TaskCenterFixture(runner);
        var job = new ConversionJob(
            ConversionAction.ToAndroid,
            new ConversionOptions { Output = new OutputOptions("/out"), Naming = MergeNamingFormat.XiaomiClean },
            new ConversionInputs { Pairs = [good, bad, forcedBad], ForceAccepted = [forcedBad] });

        var report = await fixture.Center.RunAsync(job).Within();

        Assert.Equal([bad, forcedBad], report.RetryJob.Inputs.Pairs);
        Assert.Equal([forcedBad], report.RetryJob.Inputs.ForceAccepted);
        Assert.Equal(MergeNamingFormat.XiaomiClean, report.RetryJob.Options.Naming);
    }

    [Fact]
    public async Task RetryFailed_SubmitsNewTaskThroughTaskCenter()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var center = fixture.Center;
        var first = await center.RunAsync(SplitJob()).Within();

        await first.RetryFailedCommand.ExecuteAsync(null).Within();

        Assert.Equal(2, center.History.Count);
        Assert.Same(first, center.History[1]);
        Assert.Equal(["/in/bad1.jpg", "/in/bad2.jpg"], center.History[0].Job.Inputs.Files);
        Assert.Equal(ConversionAction.ToApple, center.History[0].Action);
    }

    [Fact]
    public async Task RetryFailed_IsDisabledWhileAnotherTaskRuns()
    {
        var release = new TaskCompletionSource<BatchReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runner = new ScriptedRunner((_, _, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(MixedReport) : release.Task);
        using var fixture = new TaskCenterFixture(runner);
        var report = await fixture.Center.RunAsync(SplitJob()).Within();
        Assert.True(report.RetryFailedCommand.CanExecute(null));

        var running = fixture.Center.RunAsync(SplitJob());
        Assert.False(report.RetryFailedCommand.CanExecute(null));

        release.SetResult(Jobs.Report());
        await running.Within();
        Assert.True(report.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingFailed_CannotRetry()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report(ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg"))));
        var report = await fixture.Center.RunAsync(SplitJob()).Within();

        Assert.False(report.CanRetry);
        Assert.False(report.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task StripReport_ShowsFreedSpace()
    {
        var runner = ScriptedRunner.Returning(Jobs.Report(ItemOutcome.Succeeded("/in/a.jpg", "/in/a.heic") with { BytesSaved = 3 * 1024 * 1024 }));
        using var fixture = new TaskCenterFixture(runner);

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Strip, null, "/in/a.jpg")).Within();

        Assert.True(report.HasSavedBytes);
        Assert.Equal(fixture.Host.Localizer.Format("ReportSavedFormat", "3.0 MB"), report.SavedText);
        var item = Assert.Single(report.Items);
        Assert.Equal("HEIC", item.TargetFormat);
        Assert.Equal(fixture.Host.Localizer.Format("StripItemSavedFormat", "3.0 MB"), item.Detail);
        Assert.Equal("in-place", report.OutputDirectory);
    }

    [Fact]
    public async Task StripReport_KeptOriginalFormat_ShowsLocalizedTag()
    {
        var outcome = ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg") with
        {
            BytesSaved = 3 * 1024 * 1024,
            Notes = [new OutcomeNote(OutcomeNoteKind.KeptOriginalFormat)]
        };
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report(outcome)));

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Strip, "/out", "/in/a.jpg")).Within();

        var item = Assert.Single(report.Items);
        Assert.Equal("JPG", item.TargetFormat);
        Assert.Equal(fixture.Host.Localizer.Format("StripItemSavedFormat", "3.0 MB"), item.Detail);
        var tag = Assert.Single(item.Tags);
        Assert.Equal(fixture.Host.Localizer["OutcomeNoteKeptOriginalFormat"], tag.Text);
        Assert.False(tag.IsPositive);
    }

    [Fact]
    public async Task ExportCsv_WritesLocalizedHeaderEscapedRowsAndBom()
    {
        var report = new BatchReport(
        [
            ItemOutcome.Succeeded("/in/a,b.jpg", "/out/a,b.jpg"),
            ItemOutcome.Failed("/in/=cmd.jpg", OutcomeReason.Unexpected, "说 \"不\"")
        ], TimeSpan.FromSeconds(2), Canceled: false);
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(report));
        var vm = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a,b.jpg", "/in/=cmd.jpg")).Within();
        var target = Path.Combine(fixture.Host.Directory, "report.csv");
        fixture.Host.FilePicker.NextResult = target;

        await vm.ExportCsvCommand.ExecuteAsync(null).Within();

        var bytes = await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var localizer = fixture.Host.Localizer;
        Assert.Equal(CsvWriter.FormatRow([
            localizer["ReportCsvStatus"], localizer["ReportCsvFileName"], localizer["ReportCsvSourceFormat"], localizer["ReportCsvTargetFormat"],
            localizer["ReportCsvDetail"], localizer["ReportCsvTechnicalDetail"], localizer["ReportCsvSourcePath"], localizer["ReportCsvOutputPath"]]), lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith($"{localizer["ReportStatusFailed"]},'=cmd.jpg,JPG,", lines[1]);
        Assert.Contains("\"说 \"\"不\"\"\"", lines[1]);
        Assert.Contains("\"a,b.jpg\"", lines[2]);
        Assert.Contains("\"/out/a,b.jpg\"", lines[2]);
        Assert.Equal(localizer.Format("ExportCsvDoneFormat", target), vm.ExportStatusText);
        Assert.Equal([localizer["ExportCsvTitle"]], fixture.Host.FilePicker.Titles);
    }

    [Fact]
    public async Task ExportCsv_PickerCanceled_WritesNothing()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var vm = await fixture.Center.RunAsync(SplitJob()).Within();
        fixture.Host.FilePicker.NextResult = null;

        await vm.ExportCsvCommand.ExecuteAsync(null).Within();

        Assert.Empty(vm.ExportStatusText);
        Assert.Empty(Directory.GetFiles(fixture.Host.Directory, "*.csv"));
    }

    [Fact]
    public async Task ExportCsv_WriteFailure_ShowsAlert()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var vm = await fixture.Center.RunAsync(SplitJob()).Within();
        fixture.Host.FilePicker.NextResult = Path.Combine(fixture.Host.Directory, "missing-dir", "report.csv");

        var export = vm.ExportCsvCommand.ExecuteAsync(null);
        await WaitForAsync(() => fixture.Host.Get<IDialogService>().Current is not null);

        var alert = Assert.IsType<ConfirmDialogViewModel>(fixture.Host.Get<IDialogService>().Current);
        Assert.Equal(fixture.Host.Localizer["ExportCsvFailedTitle"], alert.Title);
        alert.ConfirmCommand.Execute(null);
        await export.Within();
    }

    [Fact]
    public async Task Metrics_ProblemsAreFailedPlusCleanupAndSkippedIsSeparate()
    {
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(MixedReport));
        var localizer = fixture.Host.Localizer;
        var report = await fixture.Center.RunAsync(SplitJob()).Within();

        // 指标卡、摘要行与图例使用同一组数：异常 = 失败 + 清理失败，跳过单列
        Assert.Equal(report.FailedCount + report.CleanupCount, report.ProblemCount);
        Assert.Equal(localizer.Format("ReportSummaryFormat", report.SuccessCount, report.ProblemCount, report.SkippedCount), report.SummaryText);
        Assert.Equal(localizer.Format("ReportKpiProblemsSubFormat", 2, 1), report.ProblemSubText);
        Assert.Equal("5", report.TotalText);
        Assert.Equal(5, report.PlannedCount);
        Assert.Equal(localizer["ReportKpiTotalSub"], report.TotalSubText);

        // "异常与跳过"筛选恰好包含异常项与跳过项
        report.SetFilterCommand.Execute(TaskReportViewModel.FilterProblems);
        Assert.Equal(report.ProblemCount + report.SkippedCount, report.FilteredItems.Count);
    }

    [Fact]
    public async Task CanceledReport_ShowsProcessedAgainstPlannedTotal()
    {
        var runner = new ScriptedRunner((_, progress, _) =>
        {
            progress!.Report(new BatchProgress(2, 50, "a.jpg"));
            return Task.FromResult(new BatchReport(
                [ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg"), ItemOutcome.Skipped("/in/b.jpg", OutcomeReason.NotMotionPhoto)],
                TimeSpan.FromSeconds(1),
                Canceled: true));
        });
        using var fixture = new TaskCenterFixture(runner);
        var files = Enumerable.Range(0, 50).Select(i => $"/in/{i}.jpg").ToArray();

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", files)).Within();

        Assert.True(report.WasCanceled);
        Assert.Equal(2, report.TotalCount);
        Assert.Equal(50, report.PlannedCount);
        Assert.Equal("2 / 50", report.TotalText);
        Assert.Equal(fixture.Host.Localizer["ReportKpiTotalCanceledSub"], report.TotalSubText);
    }

    [Fact]
    public async Task CanceledBeforeAnyProgress_UsesJobSizeAsPlannedTotal()
    {
        var runner = ScriptedRunner.Returning(new BatchReport([], TimeSpan.Zero, Canceled: true));
        using var fixture = new TaskCenterFixture(runner);

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg", "/in/b.jpg", "/in/c.jpg")).Within();

        Assert.Equal("0 / 3", report.TotalText);
    }

    [Fact]
    public async Task Notes_AreShownAsLocalizedTagsWithDetails()
    {
        var outcome = ItemOutcome.Succeeded("/in/a.heic", "/out/MVIMG_a.jpg") with
        {
            Notes = [new OutcomeNote(OutcomeNoteKind.UltraHdrWritten), new OutcomeNote(OutcomeNoteKind.HdrConversionFailed, "gain map decode failed")]
        };
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report(outcome)));
        var localizer = fixture.Host.Localizer;

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.heic")).Within();

        var item = Assert.Single(report.Items);
        Assert.True(item.HasTags);
        Assert.Equal(
            [
                new TaskReportTag(localizer["OutcomeNoteUltraHdrWritten"], null, true),
                new TaskReportTag(localizer["OutcomeNoteHdrConversionFailed"], "gain map decode failed", false)
            ],
            item.Tags);
        Assert.Contains("gain map decode failed", item.TechnicalDetail);
        Assert.True(item.HasExtras);
    }

    [Fact]
    public async Task MultipleCauses_AreJoinedOnSeparateLines()
    {
        var outcome = ItemOutcome.Skipped(
            "/in/a.jpg",
            new OutcomeCause(OutcomeReason.PairCaptureTimeClose, 1d),
            new OutcomeCause(OutcomeReason.PairVideoTooLong, 45d, 30d));
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report(outcome)));
        var localizer = fixture.Host.Localizer;

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg")).Within();

        Assert.Equal(
            localizer.Format("OutcomeReasonPairTimeCloseFormat", 1d) + Environment.NewLine + localizer.Format("OutcomeReasonPairVideoTooLongFormat", 45d, 30d),
            Assert.Single(report.Items).Detail);
    }

    [Fact]
    public async Task FatalFailure_KeepsExceptionMessageAsTechnicalDetailOnlyWhenItAddsInformation()
    {
        var runner = new ScriptedRunner((_, _, _) => Task.FromException<BatchReport>(new OutcomeException(OutcomeReason.VerificationFailed, "layout mismatch")));
        using var fixture = new TaskCenterFixture(runner);

        var report = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg")).Within();

        Assert.Equal(fixture.Host.Localizer["OutcomeReasonVerificationFailed"], report.ErrorMessage);
        var item = Assert.Single(report.Items);
        Assert.Equal(report.ErrorMessage, item.Detail);
        Assert.Equal("layout mismatch", item.TechnicalDetail);

        // 未归类的异常直接显示原文，展开区不再重复
        using var generic = new TaskCenterFixture(new ScriptedRunner((_, _, _) => Task.FromException<BatchReport>(new IOException("disk full"))));
        var genericReport = await generic.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg")).Within();
        Assert.Equal("disk full", genericReport.ErrorMessage);
        Assert.Null(Assert.Single(genericReport.Items).TechnicalDetail);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待条件超时");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
