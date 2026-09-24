using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Features.Settings;
using LivePhotoConvert.Desktop.Features.Tasks;
using LivePhotoConvert.Desktop.Features.Tools;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Features.Tasks;
using LivePhotoConvert.Desktop.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Features.Shell;

/// <summary>在 Headless 平台上加载真实外壳：页面导航、语言与主题切换、截图。</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ShellSmokeTests : IDisposable
{
    private static readonly AppPage[] Pages = [AppPage.Library, AppPage.Tasks, AppPage.Tools, AppPage.Settings];

    private readonly TestSandbox _album = new();

    public void Dispose() => _album.Dispose();

    [AvaloniaFact]
    public void EveryPage_ShowsOnlyItsOwnViewBoundToItsViewModel()
    {
        using var session = new ShellSession();

        foreach (var page in Pages)
        {
            session.Navigate(page);

            var visible = PageViews(session).Where(v => v.IsEffectivelyVisible).ToList();
            Assert.Equal(ExpectedViews(page), visible.Select(v => v.GetType()));
            Assert.All(visible, v => Assert.True(v.Bounds.Width > 0 && v.Bounds.Height > 0, $"{v.GetType().Name} 没有参与布局"));
        }

        var shell = session.Shell;
        Assert.Same(shell.Library, session.Descendants<LibraryView>().Single().DataContext);
        Assert.Same(shell.Inspector, session.Descendants<InspectorView>().Single().DataContext);
        Assert.Same(shell.Tasks, session.Descendants<TasksView>().Single().DataContext);
        Assert.Same(shell.Tools, session.Descendants<ToolsView>().Single().DataContext);
        Assert.Same(shell.Settings, session.Descendants<SettingsView>().Single().DataContext);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task LanguageSwitch_EnglishHasNoKeyNamesOrChinese_AndChineseComesBack()
    {
        using var session = await PopulatedSessionAsync("zh");
        VisitAll(session, "中文", english: false);

        session.Shell.Settings.SetLanguageCommand.Execute("en");
        session.Pump();
        Assert.Equal("en", session.Host.Settings.Current.Language);
        Assert.Equal("LivePhotoConvert", session.Window.Title);
        VisitAll(session, "切换到英文", english: true);

        session.Shell.Settings.SetLanguageCommand.Execute("zh");
        session.Pump();
        VisitAll(session, "切回中文", english: false);
        var texts = UiTexts.Collect(session).Select(t => t.Text).ToHashSet();
        Assert.Contains(session.Localizer["NavLibrary"], texts);
        Assert.Contains(session.Localizer["NavSettings"], texts);
        Assert.Equal("图库", session.Localizer["NavLibrary"]);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public void ThemeSwitch_LightAndDarkRenderDifferentBrightness()
    {
        using var session = new ShellSession();

        using var light = session.Capture();
        session.Shell.Settings.SetThemeCommand.Execute(ThemeService.Dark);
        using var dark = session.Capture();
        session.Shell.Settings.SetThemeCommand.Execute(ThemeService.Light);
        using var lightAgain = session.Capture();

        Assert.Equal(ThemeService.Light, session.Host.Settings.Current.Theme);
        if (light is null || dark is null || lightAgain is null)
        {
            Assert.Skip("Headless 平台没有渲染出帧。");
        }

        var (l, d, l2) = (Screenshots.MeanLuminance(light), Screenshots.MeanLuminance(dark), Screenshots.MeanLuminance(lightAgain));
        Assert.True(l - d > 60, $"深色主题没有明显变暗：浅色 {l:F0}，深色 {d:F0}");
        Assert.Equal(l, l2, 1.0);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public async Task Screenshots_EveryPageInBothThemesAndLanguages()
    {
        foreach (var theme in new[] { ThemeService.Light, ThemeService.Dark })
        {
            foreach (var language in new[] { "zh", "en" })
            {
                using var session = await PopulatedSessionAsync(language, theme);
                foreach (var page in Pages)
                {
                    session.Navigate(page);
                    Screenshots.Save(session, $"page-{page.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}-{language}");
                }

                session.Shell.Settings.ShowAboutCommand.Execute(null);
                session.Navigate(AppPage.Settings);
                Screenshots.Save(session, $"page-about-{theme.ToLowerInvariant()}-{language}");

                session.Navigate(AppPage.Library);
                session.Shell.Inspector.SetActionCommand.Execute(nameof(ConversionAction.Strip));
                session.Pump();
                Screenshots.Save(session, $"page-library-strip-{theme.ToLowerInvariant()}-{language}");
                session.Log.AssertNoBindingErrors();
            }
        }
    }

    /// <summary>画廊有照片、任务中心有一条带失败项的历史，截图与文案检查才能覆盖这些区域。</summary>
    private async Task<ShellSession> PopulatedSessionAsync(string language, string theme = ThemeService.Light)
    {
        if (!File.Exists(Path.Combine(_album.InputDirectory, "IMG_0001.jpg")))
        {
            SampleAlbum.WriteApplePairs(_album.InputDirectory, 7);
        }

        var runner = ScriptedRunner.Returning(new BatchReport(
        [
            ItemOutcome.Succeeded(Path.Combine(_album.InputDirectory, "IMG_0001.jpg"), Path.Combine(_album.OutputDirectory, "IMG_0001.mp4")),
            ItemOutcome.Succeeded(Path.Combine(_album.InputDirectory, "IMG_0002.jpg"), Path.Combine(_album.OutputDirectory, "IMG_0002.mp4")),
            ItemOutcome.Failed(Path.Combine(_album.InputDirectory, "IMG_0003.jpg"), "ffmpeg exited with code 1")
        ], TimeSpan.FromSeconds(3), Canceled: false));
        var session = new ShellSession(language, theme,
            configure: services => services.AddSingleton<IConversionRunner>(runner),
            settings: s =>
            {
                s.OutputDirectory = _album.OutputDirectory;
                s.AutoOpenOutput = false;
            });
        try
        {
            var library = session.Shell.Library;
            library.AlbumDirectory = _album.InputDirectory;
            await library.RefreshAlbumAsync();
            await session.WaitUntilAsync(() => library.AllCards.Count == 7);

            var report = await session.Host.Get<TaskCenter>().RunAsync(Jobs.Files(ConversionAction.Extract, _album.OutputDirectory,
                [.. library.AllCards.Take(3).Select(c => c.PhotoPath)]));
            Assert.False(report.IsFatal);
            session.Navigate(AppPage.Library);
            // 缩略图在后台解码，等到首屏卡片都有图再截图
            await session.WaitUntilAsync(() => library.AllCards.All(c => c.DisplayImage is not null));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static void VisitAll(ShellSession session, string context, bool english)
    {
        void Check(string where)
        {
            UiTexts.AssertNoResourceKeys(session, $"{context} / {where}");
            if (english)
            {
                UiTexts.AssertNoChinese(session, $"{context} / {where}");
            }
        }

        foreach (var page in Pages)
        {
            session.Navigate(page);
            Check(page.ToString());
        }

        session.Shell.Settings.ShowAboutCommand.Execute(null);
        session.Pump();
        Check("About");
        session.Shell.Settings.ShowSettingsCommand.Execute(null);

        session.Navigate(AppPage.Library);
        foreach (var action in Enum.GetValues<ConversionAction>())
        {
            session.Shell.Inspector.SetActionCommand.Execute(action.ToString());
            session.Pump();
            Check($"Inspector {action}");
        }
    }

    private static IEnumerable<UserControl> PageViews(ShellSession session) =>
        session.Descendants<UserControl>().Where(v => v is LibraryView or InspectorView or TasksView or ToolsView or SettingsView);

    private static Type[] ExpectedViews(AppPage page) => page switch
    {
        AppPage.Library => [typeof(LibraryView), typeof(InspectorView)],
        AppPage.Tasks => [typeof(TasksView)],
        AppPage.Tools => [typeof(ToolsView)],
        _ => [typeof(SettingsView)]
    };
}
