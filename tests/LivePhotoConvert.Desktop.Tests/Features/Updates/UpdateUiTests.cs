using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Desktop.Features.Dialogs;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Features.Updates;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Updates;

/// <summary>更新相关界面在真实外壳中的冒烟：设置页"更新"分组、侧栏小圆点、关于页提示与新版本弹窗的各阶段（中英、浅深色截图）。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class UpdateUiTests
{
    /// <summary>更新说明是发布数据，语言随 CHANGELOG；英文界面用例配英文说明，以便检查界面没有残留中文。</summary>
    private static string NotesFor(string language) => language == "en" ? EnglishNotes : ChineseNotes;

    private const string EnglishNotes = """
        ### Added
        - Windows installer and automatic updates: checks in the background and tells you when a new version is out
        - New **Updates** group in Preferences with a manual check
          - Skip a version you don't want
        ### Fixed
        - Dependencies now live in `%LocalAppData%\LivePhotoConvert\tools`, so updates keep downloaded tools
        """;

    private const string ChineseNotes = """
        ### 新增
        - Windows 安装版与自动更新：启动后在后台检查，发现新版本时提醒
        - 设置页新增 **更新** 分组，可手动检查
          - 支持跳过某个版本
        ### 修复
        - 依赖工具改装到 `%LocalAppData%\LivePhotoConvert\tools`，更新不再丢失已下载的工具
        """;

    public static TheoryData<string, string> LanguagesAndThemes => new()
    {
        { "zh", ThemeService.Light },
        { "zh", ThemeService.Dark },
        { "en", ThemeService.Light },
        { "en", ThemeService.Dark }
    };

    [AvaloniaTheory]
    [MemberData(nameof(LanguagesAndThemes))]
    public void SettingsUpdateGroup_ShowsVersionStatusAndActions(string language, string theme)
    {
        var updates = new FakeUpdateService();
        using var session = new ShellSession(language, theme, Replace(updates), s =>
        {
            s.Updates.LastCheckTime = new DateTimeOffset(2026, 9, 23, 20, 30, 0, TimeSpan.Zero);
            s.Updates.LastCheckStatus = UpdateCheckStatus.UpdateAvailable;
            s.Updates.LastAvailableVersion = "3.1.0";
        });
        session.Navigate(AppPage.Settings);
        BringIntoView(session, "UpdateSettingsCard");

        var texts = UiTexts.Collect(session).Select(t => t.Text).ToHashSet();
        foreach (var key in (string[])["SettingsUpdatesTitle", "UpdateCheckBtn", "UpdateShowBtn", "UpdateAutoCheckTitle", "UpdateAutoCheckDesc"])
        {
            Assert.Contains(session.Localizer[key], texts);
        }

        Assert.Contains(session.Shell.Updates.StatusText, texts);
        Assert.Contains(session.Localizer.Format("UpdateResultAvailableFormat", "3.1.0"), session.Shell.Updates.StatusText);
        Assert.True(Named<Button>(session, "CheckUpdateButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(session, "ShowUpdateButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(session, "OpenDownloadPageButton").IsEffectivelyVisible);
        Assert.True(Named<ToggleSwitch>(session, "AutoCheckUpdateSwitch").IsChecked);

        // 侧栏"设置"小圆点带提示
        var dot = Named<Border>(session, "UpdateAvailableDot");
        Assert.True(dot.IsEffectivelyVisible);
        Assert.Equal(session.Localizer.Format("UpdateAvailableTipFormat", "3.1.0"), ToolTip.GetTip(dot));

        AssertLocalized(session, language, "设置页更新分组");
        Screenshots.Save(session, $"settings-updates-{theme.ToLowerInvariant()}-{language}");

        // 关闭自动检查写入设置
        session.Click(Named<ToggleSwitch>(session, "AutoCheckUpdateSwitch"));
        Assert.False(session.Host.Settings.Current.Updates.AutoCheck);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("en")]
    public void Unsupported_ExplainsAndOffersDownloadPage(string language)
    {
        // 测试进程没有 Velopack 安装信息：产品组合根里的真实服务报告不支持
        using var session = new ShellSession(language);
        Assert.False(session.Host.Get<IUpdateService>().IsSupported);
        session.Navigate(AppPage.Settings);
        BringIntoView(session, "UpdateSettingsCard");

        Assert.Equal(session.Localizer["UpdateUnsupportedDesc"], Named<TextBlock>(session, "UpdateStatusText").Text);
        Assert.False(Named<Button>(session, "CheckUpdateButton").IsEffectivelyVisible);
        Assert.False(Named<ToggleSwitch>(session, "AutoCheckUpdateSwitch").IsEffectivelyVisible);
        Assert.False(Named<Border>(session, "UpdateAvailableDot").IsEffectivelyVisible);

        session.Click(Named<Button>(session, "OpenDownloadPageButton"));
        Assert.Equal(AboutInfo.ReleasesUrl, Assert.Single(session.Host.Shell.Requests));

        AssertLocalized(session, language, "设置页更新分组（不支持）");
        Screenshots.Save(session, $"settings-updates-unsupported-{language}");
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task Indicator_AppearsAfterCheck_AndOpensDialogFromAboutPage()
    {
        var updates = new FakeUpdateService { NextUpdate = new AvailableUpdate("3.1.0", ChineseNotes, 18_500_000, true) };
        using var session = new ShellSession(configure: Replace(updates));
        var center = session.Shell.Updates;
        var dot = Named<Border>(session, "UpdateAvailableDot");
        Assert.False(dot.IsEffectivelyVisible);

        var check = center.CheckNowCommand.ExecuteAsync(null);
        await session.WaitUntilAsync(() => session.Dialogs.Current is UpdateDialogViewModel);
        session.PressEscape();
        await check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Pump();
        Assert.True(dot.IsEffectivelyVisible);

        session.Navigate(AppPage.Settings);
        session.Shell.Settings.ShowAboutCommand.Execute(null);
        session.Pump();
        var badge = Named<Button>(session, "AboutUpdateBadge");
        Assert.True(badge.IsEffectivelyVisible);
        Screenshots.Save(session, "about-update-available-light-zh");

        session.Click(badge);
        await session.WaitUntilAsync(() => session.Dialogs.Current is UpdateDialogViewModel);
        Assert.Equal(1, updates.CheckCalls);
        session.PressEscape();
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>弹窗依次经过：提示 → 下载中 → 完成（空闲 / 有任务运行）→ 失败，每个阶段截图。</summary>
    [AvaloniaTheory]
    [MemberData(nameof(LanguagesAndThemes))]
    public async Task UpdateDialog_RendersEveryStage(string language, string theme)
    {
        var updates = new FakeUpdateService
        {
            NextUpdate = new AvailableUpdate("3.1.0", NotesFor(language), 18_500_000, true),
            DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var session = new ShellSession(language, theme, Replace(updates));
        var check = session.Shell.Updates.CheckNowCommand.ExecuteAsync(null);
        await session.WaitUntilAsync(() => session.Dialogs.Current is UpdateDialogViewModel);
        var dialog = (UpdateDialogViewModel)session.Dialogs.Current!;
        session.Pump();

        var view = Assert.Single(session.Descendants<UpdateDialog>());
        Assert.True(view.Bounds.Height > 0);
        Assert.Equal(session.Localizer.Format("UpdateDialogTitleFormat", "3.1.0"), Named<TextBlock>(session, "UpdateDialogTitle").Text);
        Assert.Contains(session.Localizer.Format("UpdateSizeDeltaFormat", "17.6 MB"), UiTexts.Collect(session).Select(t => t.Text));
        Assert.True(Named<Button>(session, "SkipVersionButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(session, "LaterButton").IsEffectivelyVisible);
        AssertLocalized(session, language, "更新弹窗");
        Screenshots.Save(session, $"dialog-update-prompt-{theme.ToLowerInvariant()}-{language}");

        session.Click(Named<Button>(session, "UpdateNowButton"));
        await session.WaitUntilAsync(() => dialog.Progress == 42);
        Assert.True(Named<ProgressBar>(session, "UpdateProgress").IsEffectivelyVisible);
        Assert.True(Named<Button>(session, "CancelDownloadButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(session, "UpdateNowButton").IsEffectivelyVisible);
        Screenshots.Save(session, $"dialog-update-downloading-{theme.ToLowerInvariant()}-{language}");

        updates.DownloadGate.SetResult();
        await session.WaitUntilAsync(() => dialog.IsReady);
        Assert.True(Named<Button>(session, "RestartNowButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(session, "InstallOnExitButton").IsEffectivelyVisible);
        AssertLocalized(session, language, "更新弹窗（完成）");
        Screenshots.Save(session, $"dialog-update-ready-{theme.ToLowerInvariant()}-{language}");

        session.Click(Named<Button>(session, "InstallOnExitButton"));
        await check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, updates.ApplyOnExitCalls);

        // 有任务运行时的完成状态：等待任务完成后更新 / 取消任务立即更新
        var work = new FakeBackgroundWork { IsBusy = true };
        var busy = new UpdateDialogViewModel(updates, updates.NextUpdate, session.Localizer, [work]);
        var busyResult = session.Dialogs.ShowAsync(busy);
        busy.UpdateNowCommand.Execute(null);
        await busy.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Pump();
        Assert.True(Named<Button>(session, "RestartAfterTasksButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(session, "CancelTasksAndRestartButton").IsEffectivelyVisible);
        AssertLocalized(session, language, "更新弹窗（任务运行中）");
        Screenshots.Save(session, $"dialog-update-ready-tasks-{theme.ToLowerInvariant()}-{language}");
        session.Click(Named<Button>(session, "RestartAfterTasksButton"));
        Assert.Equal(UpdateDialogResult.RestartAfterTasks, await busyResult.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // 下载失败：显示原因，可以重试
        updates.DownloadFailure = new UpdateException(UpdateFailureKind.Network, "offline");
        var failing = new UpdateDialogViewModel(updates, updates.NextUpdate, session.Localizer, []);
        var failingResult = session.Dialogs.ShowAsync(failing);
        failing.UpdateNowCommand.Execute(null);
        await failing.DownloadTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Pump();
        Assert.Contains(session.Localizer["UpdateFailureNetwork"], UiTexts.Collect(session).Select(t => t.Text));
        Assert.True(Named<Button>(session, "UpdateNowButton").IsEffectivelyVisible);
        Screenshots.Save(session, $"dialog-update-failed-{theme.ToLowerInvariant()}-{language}");
        session.PressEscape();
        Assert.Equal(UpdateDialogResult.Later, await failingResult.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        session.Log.AssertNoBindingErrors();
    }

    private static Action<IServiceCollection> Replace(FakeUpdateService updates) => services => services.AddSingleton<IUpdateService>(updates);

    private static T Named<T>(ShellSession session, string name) where T : Control =>
        Assert.Single(session.Descendants<T>(), c => c.Name == name);

    private static void BringIntoView(ShellSession session, string name)
    {
        Named<Control>(session, name).BringIntoView();
        session.Pump();
    }

    private static void AssertLocalized(ShellSession session, string language, string context)
    {
        UiTexts.AssertNoResourceKeys(session, context);
        if (language == "en")
        {
            UiTexts.AssertNoChinese(session, context);
        }
    }
}
