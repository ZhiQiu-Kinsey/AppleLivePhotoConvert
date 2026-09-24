using Avalonia;
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

    /// <summary>
    /// 数千项的报告只实例化可见的行（未虚拟化时 3000 项要创建数万个控件、耗时数十秒）；
    /// 行控件复用给其它条目后，展开状态跟随条目而不是残留在控件上。
    /// </summary>
    [AvaloniaFact]
    public async Task LargeReport_RealizesVisibleRowsOnlyAndKeepsExpansionPerItem()
    {
        const int count = 2000;
        var outcomes = Enumerable.Range(0, count)
            .Select(i => i % 2 == 0
                ? ItemOutcome.Failed($"/in/item-{i:D4}.jpg", OutcomeReason.VideoConversionFailed, $"detail {i:D4}")
                : ItemOutcome.Succeeded($"/in/item-{i:D4}.jpg", $"/out/item-{i:D4}.jpg"))
            .ToArray();
        var runner = ScriptedRunner.Returning(Jobs.Report(outcomes));
        using var session = new ShellSession("en", configure: services => services.AddSingleton<IConversionRunner>(runner));

        var report = await session.Host.Get<TaskCenter>().RunAsync(Jobs.Files(ConversionAction.Extract, "/out", [.. outcomes.Select(o => o.Source)]));
        session.Navigate(AppPage.Tasks);

        var rows = RealizedFileNames(session);
        Assert.Contains("item-0000.jpg", rows);
        Assert.True(rows.Count < 100, $"实例化了 {rows.Count} 行");

        var first = report.FilteredItems[0];
        session.Click(session.Descendants<ToggleButton>().First(t => t.Classes.Contains("detail-toggle") && t.IsEffectivelyVisible));
        Assert.True(first.IsDetailExpanded);

        var scroll = session.Descendants<ScrollViewer>().First(v => v.Content is TaskReportView);
        for (var i = 0; i < 5 && !RealizedFileNames(session).Contains("item-1999.jpg"); i++)
        {
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            session.Pump();
        }

        Assert.Contains("item-1999.jpg", RealizedFileNames(session));
        Assert.DoesNotContain(session.Descendants<SelectableTextBlock>(), t => t.IsEffectivelyVisible);
        Assert.Single(report.FilteredItems, i => i.IsDetailExpanded);

        scroll.Offset = default;
        session.Pump();
        Assert.Contains(session.Descendants<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == "detail 0000");
        session.Log.AssertNoBindingErrors();
    }

    private static List<string?> RealizedFileNames(ShellSession session) =>
        [.. session.Descendants<TextBlock>().Select(t => t.Text).Where(t => t?.StartsWith("item-", StringComparison.Ordinal) == true)];

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
