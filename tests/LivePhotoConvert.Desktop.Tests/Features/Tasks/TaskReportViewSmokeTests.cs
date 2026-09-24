using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

/// <summary>报告明细在真实视图中的附注标签与可展开的技术细节。</summary>
[Collection(ProcessStateCollection.Name)]
public class TaskReportViewSmokeTests
{
    [AvaloniaFact]
    public async Task ReportItem_ShowsNoteTagsAndExpandsTechnicalDetail()
    {
        var runner = ScriptedRunner.Returning(Jobs.Report(
            ItemOutcome.Failed("/in/a.jpg", OutcomeReason.VideoConversionFailed, "ffmpeg exited with code 1"),
            ItemOutcome.Succeeded("/in/b.heic", "/out/b.jpg") with { Notes = [new OutcomeNote(OutcomeNoteKind.UltraHdrWritten)] }));
        using var session = new ShellSession("en", configure: services => services.AddSingleton<IConversionRunner>(runner));

        await session.Host.Get<TaskCenter>().RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg", "/in/b.heic"));
        session.Navigate(AppPage.Tasks);

        var texts = session.Descendants<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(session.Localizer["OutcomeReasonVideoConversionFailed"], texts);
        Assert.Contains(session.Localizer["OutcomeNoteUltraHdrWritten"], texts);

        var detail = session.Descendants<SelectableTextBlock>().Single(t => t.Text == "ffmpeg exited with code 1");
        Assert.False(detail.IsEffectivelyVisible);
        var toggle = session.Descendants<ToggleButton>().Single(t => t.Classes.Contains("detail-toggle") && t.IsEffectivelyVisible);

        session.Click(toggle);

        Assert.True(detail.IsEffectivelyVisible);
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>取消的任务：处理总量显示计划总数，副标题分列已处理与未处理。</summary>
    [AvaloniaTheory]
    [InlineData("zh", ThemeService.Light)]
    [InlineData("en", ThemeService.Dark)]
    public async Task CanceledReport_ShowsPlannedTotalWithProcessedAndUnprocessed(string language, string theme)
    {
        var runner = new ScriptedRunner((_, progress, _) =>
        {
            progress!.Report(new BatchProgress(2, 6, "b.jpg"));
            return Task.FromResult(new BatchReport(
                [ItemOutcome.Succeeded("/in/a.jpg", "/out/a.jpg"), ItemOutcome.Skipped("/in/b.jpg", OutcomeReason.NotMotionPhoto)],
                TimeSpan.FromSeconds(2),
                Canceled: true));
        });
        using var session = new ShellSession(language, theme, configure: services => services.AddSingleton<IConversionRunner>(runner));
        var files = Enumerable.Range(0, 6).Select(i => $"/in/{i}.jpg").ToArray();

        await session.Host.Get<TaskCenter>().RunAsync(Jobs.Files(ConversionAction.Extract, "/out", files));
        session.Navigate(AppPage.Tasks);

        var texts = session.Descendants<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("6", texts);
        Assert.Contains(session.Localizer.Format("ReportKpiTotalCanceledSubFormat", 2, 4), texts);
        Screenshots.Save(session, $"report-canceled-{theme.ToLowerInvariant()}-{language}");
        session.Log.AssertNoBindingErrors();
    }
}
