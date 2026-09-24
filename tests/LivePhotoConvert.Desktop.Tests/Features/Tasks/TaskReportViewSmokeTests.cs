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
}
