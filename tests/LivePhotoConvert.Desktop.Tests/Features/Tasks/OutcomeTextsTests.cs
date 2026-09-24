using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Tasks;

/// <summary>Core 原因码与附注的本地化，以及切换语言后报告文案的刷新。</summary>
[Collection(ProcessStateCollection.Name)]
public class OutcomeTextsTests
{
    /// <summary>覆盖各原因码的全部参数形态：字符串参数与秒数参数。</summary>
    private static readonly object[] SampleArguments = ["IMG_0001.HEIC", 12.5d];

    [Fact]
    public void EveryReasonAndNoteKind_HasOwnKeyInBothDictionaries()
    {
        var zh = DesktopSources.LoadStrings("zh-CN");
        var en = DesktopSources.LoadStrings("en-US");
        var reasonKeys = Enum.GetValues<OutcomeReason>().Select(OutcomeTexts.ReasonKey).ToList();
        var noteKeys = Enum.GetValues<OutcomeNoteKind>().Select(OutcomeTexts.NoteKey).ToList();

        // 键互不相同，说明没有枚举值落入默认分支
        Assert.Equal(reasonKeys.Count, reasonKeys.Distinct().Count());
        Assert.Equal(noteKeys.Count, noteKeys.Distinct().Count());
        foreach (var key in reasonKeys.Concat(noteKeys))
        {
            Assert.True(zh.ContainsKey(key), $"zh-CN 缺少 {key}");
            Assert.True(en.ContainsKey(key), $"en-US 缺少 {key}");
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void EveryReasonAndNoteKind_HasNonEmptyFormattedText(string language)
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();
        localizer.SetLanguage(language);

        foreach (var reason in Enum.GetValues<OutcomeReason>())
        {
            var text = OutcomeTexts.Describe(localizer, new OutcomeCause(reason, SampleArguments));
            Assert.False(string.IsNullOrWhiteSpace(text), $"{language} {reason} 文案为空");
            Assert.NotEqual(OutcomeTexts.ReasonKey(reason), text);
            Assert.DoesNotContain("{", text);
            Assert.Equal(language == "en-US", !UiTexts.ContainsChinese(text));
        }

        foreach (var kind in Enum.GetValues<OutcomeNoteKind>())
        {
            var text = OutcomeTexts.Describe(localizer, kind);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{language} {kind} 文案为空");
            Assert.NotEqual(OutcomeTexts.NoteKey(kind), text);
            Assert.False(language == "en-US" && UiTexts.ContainsChinese(text), $"{kind} 英文文案含中文");
        }
    }

    [Fact]
    public void ErrorMessages_LocalizesClassifiedExceptionsAndKeepsOthers()
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();
        localizer.SetLanguage("en-US");

        Assert.Equal(localizer.Format("ToolMissingFormat", "ffmpeg"), ErrorMessages.Describe(localizer, new ToolNotFoundException("ffmpeg")));
        Assert.Equal(
            localizer["OutcomeReasonHdrEncoderUnavailable"],
            ErrorMessages.Describe(localizer, new VideoConversionException(VideoConversionError.HdrEncoderUnavailable, "缺少 libx265")));
        Assert.Equal(
            localizer["OutcomeReasonVerificationFailed"],
            ErrorMessages.Describe(localizer, new OutcomeException(OutcomeReason.VerificationFailed, "校验失败")));
        Assert.Equal("disk full", ErrorMessages.Describe(localizer, new IOException("disk full")));
    }

    [Fact]
    public async Task LanguageSwitch_RefreshesItemReasonsAndMetrics()
    {
        using var _ = new CultureScope();
        var report = new BatchReport(
        [
            ItemOutcome.Skipped("/in/a.jpg", new OutcomeCause(OutcomeReason.PairCaptureTimeTooFar, 12d, 3d)),
            ItemOutcome.Failed("/in/b.jpg", OutcomeReason.VideoConversionFailed, "ffmpeg exited with code 1"),
            ItemOutcome.Succeeded("/in/c.heic", "/out/c.jpg") with { Notes = [new OutcomeNote(OutcomeNoteKind.HdrGainMapMissing)] }
        ], TimeSpan.FromSeconds(3), Canceled: false);
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(report));
        var localizer = fixture.Host.Localizer;
        localizer.SetLanguage("zh-CN");
        var vm = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg", "/in/b.jpg", "/in/c.heic")).Within();

        Assert.Equal("拍摄时间差 12 秒，超过 3 秒阈值", vm.Items.Single(i => i.Status == TaskItemStatus.Skipped).Detail);
        Assert.Equal("视频转换失败", vm.Items.Single(i => i.IsFailed).Detail);
        Assert.Equal("无 HDR 增益图", Assert.Single(vm.Items.Single(i => i.IsSuccess).Tags).Text);
        Assert.Equal("成功 1 项 · 异常 1 项 · 跳过 1 项", vm.SummaryText);

        localizer.SetLanguage("en-US");

        Assert.Equal("Capture times differ by 12 s, over the 3 s limit", vm.Items.Single(i => i.Status == TaskItemStatus.Skipped).Detail);
        Assert.Equal("Video conversion failed", vm.Items.Single(i => i.IsFailed).Detail);
        Assert.Equal("ffmpeg exited with code 1", vm.Items.Single(i => i.IsFailed).TechnicalDetail);
        Assert.Equal("No HDR gain map", Assert.Single(vm.Items.Single(i => i.IsSuccess).Tags).Text);
        Assert.Equal("Succeeded 1 · Issues 1 · Skipped 1", vm.SummaryText);
        Assert.Equal("Failed 1 · Cleanup 0", vm.ProblemSubText);
        Assert.All(vm.FilteredItems.Select(i => i.Detail), text => Assert.False(UiTexts.ContainsChinese(text), text));
    }

    [Fact]
    public async Task LanguageSwitch_RefreshesMergeTargetAndDuration()
    {
        using var _ = new CultureScope();
        var pair = new MediaPair("/in/a.heic", "/in/a.mov");
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report(ItemOutcome.Succeeded(pair.PhotoPath, "/out/a.jpg"))));
        var localizer = fixture.Host.Localizer;
        localizer.SetLanguage("zh-CN");
        var job = new ConversionJob(ConversionAction.ToAndroid, new ConversionOptions { Output = new OutputOptions("/out") }, new ConversionInputs { Pairs = [pair] });
        var vm = await fixture.Center.RunAsync(job).Within();

        Assert.Equal("动态照片", Assert.Single(vm.Items).TargetFormat);
        Assert.Equal("4.0 秒", vm.DurationText);

        localizer.SetLanguage("en-US");

        Assert.Equal("Motion Photo", Assert.Single(vm.Items).TargetFormat);
        Assert.Equal("4.0s", vm.DurationText);
    }

    [Fact]
    public async Task LanguageSwitch_RefreshesFatalFailureMessage()
    {
        using var _ = new CultureScope();
        var runner = new ScriptedRunner((_, _, _) => Task.FromException<BatchReport>(new ToolNotFoundException("exiftool")));
        using var fixture = new TaskCenterFixture(runner);
        var localizer = fixture.Host.Localizer;
        localizer.SetLanguage("zh-CN");
        var vm = await fixture.Center.RunAsync(Jobs.Files(ConversionAction.Extract, "/out", "/in/a.jpg")).Within();
        Assert.Equal("未找到 exiftool，请在「依赖引擎」页面下载或指定路径。", vm.ErrorMessage);

        localizer.SetLanguage("en-US");

        Assert.Equal(localizer.Format("ToolMissingFormat", "exiftool"), vm.ErrorMessage);
        Assert.Equal(vm.ErrorMessage, Assert.Single(vm.Items).Detail);
        Assert.False(UiTexts.ContainsChinese(vm.ErrorMessage));
    }

    [Fact]
    public async Task LanguageSwitch_RefreshesEmptyInputMessage()
    {
        using var _ = new CultureScope();
        using var fixture = new TaskCenterFixture(ScriptedRunner.Returning(Jobs.Report()));
        var localizer = fixture.Host.Localizer;
        localizer.SetLanguage("zh-CN");
        var vm = await fixture.Center.RunAsync(new ConversionJob(ConversionAction.ToAndroid, new ConversionOptions(), new ConversionInputs())).Within();
        var chinese = vm.ErrorMessage;

        localizer.SetLanguage("en-US");

        Assert.NotEqual(chinese, vm.ErrorMessage);
        Assert.Equal(localizer["NoMergePairs"], vm.ErrorMessage);
        Assert.Null(Assert.Single(vm.Items).TechnicalDetail);
    }
}
