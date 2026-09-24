using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Tools;

/// <summary>依赖页在 Headless 平台上的布局、文案与截图：升级提示、能力徽章、缺失工具与安装进度。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ToolsViewSmokeTests
{
    [AvaloniaFact]
    public async Task ToolsPage_BothLanguagesAndThemes_HaveNoMissingKeysAndRenderCards()
    {
        foreach (var theme in new[] { ThemeService.Light, ThemeService.Dark })
        {
            foreach (var language in new[] { "zh", "en" })
            {
                var installer = new FakeToolInstaller();
                using var session = Open(language, theme, installer);
                var tools = session.Shell.Tools;
                var suffix = $"{theme.ToLowerInvariant()}-{language}";

                CheckTexts(session, language, "依赖页");
                // 页头之下不再重复标题（侧栏的依赖状态面板不属于本页）
                var page = session.Descendants<ToolsView>().Single();
                Assert.Equal(1, page.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == session.Localizer["ToolsTitle"] && t.IsEffectivelyVisible));
                Assert.Equal(2, session.Descendants<Border>().Count(b => b.Classes.Contains("capability-badge") && b.IsEffectivelyVisible));
                var pathButtons = session.Descendants<Button>().Where(b => b.Command == tools.ExifTool.PickPathCommand || b.Command == tools.Ffmpeg.PickPathCommand || b.Command == tools.HeifEnc.PickPathCommand).ToList();
                Assert.Equal(3, pathButtons.Count);
                Assert.All(pathButtons, b => Assert.Contains("engine-secondary-btn", b.Classes));
                var badge = session.Descendants<TextBlock>().Single(t => t.Text == "7.0");
                Assert.True(badge.Bounds.Width >= badge.DesiredSize.Width - 0.5, "版本徽章被截断");
                Screenshots.Save(session, $"tools-{suffix}");

                // 安装中：进度与取消按钮替换安装按钮
                var pending = new TaskCompletionSource<ToolInstallResult>();
                installer.Script = (tool, _, progress, _) =>
                {
                    progress!.Report(new ToolInstallProgress(tool, ToolInstallStage.Downloading, installer.Candidate(tool), 1, 1, 12L << 20, 30L << 20, 3.5 * (1 << 20)));
                    return pending.Task;
                };
                var install = tools.InstallAsync(ToolId.HeifEnc);
                await session.WaitUntilAsync(() => tools.HeifEnc.ProgressValue > 0);
                Assert.True(tools.HeifEnc.IsInstalling);
                CheckTexts(session, language, "安装中");
                Screenshots.Save(session, $"tools-installing-{suffix}");

                pending.SetResult(FakeToolInstaller.Result(ToolId.HeifEnc, installer.ExecutablePath(ToolId.HeifEnc)));
                await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                session.Pump();
                session.Log.AssertNoBindingErrors();
            }
        }
    }

    [AvaloniaFact]
    public void ToolsPage_UnsupportedPlatform_ShowsDisabledPackageManagerHint()
    {
        using var session = Open("zh", ThemeService.Light, new FakeToolInstaller(platformSupported: false));

        var hint = session.Descendants<TextBlock>().Single(t => t.Text == session.Localizer["ToolPlatformUnsupportedBtn"] && t.IsEffectivelyVisible);
        var button = hint.FindAncestorOfType<Button>();
        Assert.NotNull(button);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.DoesNotContain(session.Descendants<TextBlock>(), t => t.Text == session.Localizer["InstallToolBtn"] && t.IsEffectivelyVisible);
        Screenshots.Save(session, "tools-unsupported-light-zh");
        session.Log.AssertNoBindingErrors();
    }

    private static ShellSession Open(string language, string theme, FakeToolInstaller installer)
    {
        var registry = new FakeToolRegistry();
        registry.Infos[ToolId.ExifTool] = FakeToolRegistry.Found(ToolId.ExifTool, "/usr/bin/exiftool", "12.76", recommended: "13.59");
        registry.Infos[ToolId.Ffmpeg] = FakeToolRegistry.Found(ToolId.Ffmpeg, "/usr/local/bin/ffmpeg", "n7.0-7-gd38bf5e08e-20240407",
            ToolCapabilities.Zscale | ToolCapabilities.Tonemap | ToolCapabilities.Libx264 | ToolCapabilities.Libx265, recommended: "7.0");
        registry.Infos[ToolId.HeifEnc] = FakeToolRegistry.Missing(ToolId.HeifEnc, "1.23.4");

        var session = new ShellSession(language, theme, services =>
        {
            services.AddSingleton<IToolRegistry>(registry);
            services.AddSingleton<IToolInstaller>(installer);
        });
        session.Navigate(AppPage.Tools);
        return session;
    }

    private static void CheckTexts(ShellSession session, string language, string context)
    {
        UiTexts.AssertNoResourceKeys(session, context);
        if (language == "en")
        {
            UiTexts.AssertNoChinese(session, context);
        }
    }
}
