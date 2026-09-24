using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Shell;
using LivePhotoConvert.Desktop.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// 在 Headless 平台上显示真实的 <see cref="ShellWindow"/>：服务来自产品组合根，只替换设置文件、选择器与外壳调用。
/// 只能在 [AvaloniaFact] 用例的界面线程上使用。
/// </summary>
public sealed class ShellSession : IDisposable
{
    private readonly CultureScope _culture = new();

    /// <param name="configure">在测试宿主的替换之后执行，可继续替换服务。</param>
    /// <param name="settings">在任何页面创建之前修改设置。</param>
    /// <param name="language">设置中保存的语言代码（"zh" / "en"）。</param>
    public ShellSession(string language = "zh", string theme = ThemeService.Light,
        Action<IServiceCollection>? configure = null, Action<DesktopSettings>? settings = null)
    {
        Log = new UiLogSink();
        Host = new DesktopTestHost(configure);
        Host.Settings.Update(s =>
        {
            s.Language = language;
            s.Theme = theme;
            settings?.Invoke(s);
        });

        // 与 App.OnFrameworkInitializationCompleted 相同的顺序：主题与语言先于任何页面 VM 生效。
        // 不挂 AppLifetime：关窗收尾会清理本进程临时目录，影响同进程其它用例。
        Theme.Apply(Host.Settings.Current.Theme);
        Localizer.SetLanguage(Host.Settings.Current.Language);

        Window = new ShellWindow { DataContext = Host.Get<ShellViewModel>() };
        Window.Show();
        Pump();
    }

    public DesktopTestHost Host { get; }

    public UiLogSink Log { get; }

    public ShellWindow Window { get; }

    public ShellViewModel Shell => (ShellViewModel)Window.DataContext!;

    public ILocalizer Localizer => Host.Localizer;

    public ThemeService Theme => Host.Get<ThemeService>();

    public IDialogService Dialogs => Host.Get<IDialogService>();

    public void Navigate(AppPage page)
    {
        Shell.NavigateCommand.Execute(page.ToString());
        Pump();
    }

    /// <summary>执行已排队的界面任务并完成一次布局与渲染。</summary>
    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>等待后台工作（扫描、缩略图、弹窗结果）经界面线程交付；await 期间调度器继续处理队列。</summary>
    public async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待界面状态超时。");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
            Pump();
        }
    }

    public WriteableBitmap? Capture()
    {
        Pump();
        return Window.CaptureRenderedFrame();
    }

    public IEnumerable<T> Descendants<T>() where T : Visual => Window.GetVisualDescendants().OfType<T>();

    /// <summary>可视树与逻辑树的并集：未展开的下拉项等只存在于逻辑树。</summary>
    public IEnumerable<StyledElement> AllElements() =>
        Window.GetVisualDescendants().OfType<StyledElement>()
            .Concat(Window.GetLogicalDescendants().OfType<StyledElement>())
            .Distinct();

    /// <summary>用真实的鼠标移入、按下、抬起点击控件中心；控件被遮挡时点击落不到它身上，调用方对结果的断言随之失败。</summary>
    public void Click(Control control)
    {
        // 先让排队的布局生效，否则按的是控件上一次布局的位置
        Pump();
        Assert.True(control.IsEffectivelyVisible, $"{control.GetType().Name} 不可见");
        Assert.True(control.IsEffectivelyEnabled, $"{control.GetType().Name} 不可用");
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException("控件不在窗口内。");
        Window.MouseMove(center);
        Window.MouseDown(center, MouseButton.Left);
        Window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    public void PressEscape()
    {
        Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
    }

    public void Dispose()
    {
        try
        {
            Dialogs.CancelAll();
            Window.Close();
            Pump();
        }
        finally
        {
            Host.Dispose();
            Log.Dispose();
            _culture.Dispose();
        }
    }
}
