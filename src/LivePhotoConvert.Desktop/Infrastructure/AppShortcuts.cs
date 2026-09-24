using Avalonia.Input;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 一个快捷键：按键匹配与界面提示共用同一份定义，提示文本不会与实际绑定脱节。
/// </summary>
public sealed class AppShortcut
{
    internal AppShortcut(params KeyGesture[] gestures)
    {
        Gestures = gestures;
        DisplayText = Describe(gestures[0]);
    }

    /// <summary>触发该快捷键的按键组合；第一项用于显示。</summary>
    public IReadOnlyList<KeyGesture> Gestures { get; }

    /// <summary>提示中显示的按键，如 "Ctrl+O"、"F5"。</summary>
    public string DisplayText { get; }

    public bool Matches(KeyEventArgs e) => Matches(e.Key, e.KeyModifiers);

    /// <summary>修饰键必须完全一致；macOS 的 Command（Meta）按 Ctrl 处理。</summary>
    public bool Matches(Key key, KeyModifiers modifiers)
    {
        var normalized = Normalize(modifiers);
        foreach (var gesture in Gestures)
        {
            if (gesture.Key == key && gesture.KeyModifiers == normalized)
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => DisplayText;

    internal static KeyModifiers Normalize(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Meta) ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control : modifiers;

    /// <summary>与平台无关的写法：Meta 已并入 Ctrl，各平台提示一致。</summary>
    private static string Describe(KeyGesture gesture)
    {
        var prefix = (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : string.Empty) +
                     (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : string.Empty) +
                     (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : string.Empty);
        var key = gesture.Key switch
        {
            Key.Escape => "Esc",
            Key.Enter => "Enter",
            Key.Space => "Space",
            Key.Left => "←",
            Key.Right => "→",
            >= Key.D0 and <= Key.D9 => ((int)(gesture.Key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => gesture.Key.ToString()
        };
        return prefix + key;
    }
}

/// <summary>应用内全部快捷键的唯一定义处；键盘处理与工具提示都从这里取。</summary>
public static class AppShortcuts
{
    // ── 全局（主窗口，无弹窗时） ──

    /// <summary>选择相册目录；任何页面都可用，选择前先切回图库。</summary>
    public static AppShortcut OpenAlbum { get; } = new(new KeyGesture(Key.O, KeyModifiers.Control));

    /// <summary>重新扫描当前相册（图库页）。</summary>
    public static AppShortcut Rescan { get; } = new(new KeyGesture(Key.F5));

    /// <summary>开始检查器当前动作（图库页；焦点在输入框或键盘选中的按钮上时留给该控件）。</summary>
    public static AppShortcut StartAction { get; } = new(new KeyGesture(Key.Enter));

    public static AppShortcut NavigateLibrary { get; } = Page(Key.D1, Key.NumPad1);

    public static AppShortcut NavigateTasks { get; } = Page(Key.D2, Key.NumPad2);

    public static AppShortcut NavigateTools { get; } = Page(Key.D3, Key.NumPad3);

    public static AppShortcut NavigateSettings { get; } = Page(Key.D4, Key.NumPad4);

    /// <summary>侧栏导航的快捷键与目标页。</summary>
    public static IReadOnlyList<(AppShortcut Shortcut, AppPage Page)> Pages { get; } =
    [
        (NavigateLibrary, AppPage.Library),
        (NavigateTasks, AppPage.Tasks),
        (NavigateTools, AppPage.Tools),
        (NavigateSettings, AppPage.Settings)
    ];

    // ── 画廊（焦点在画廊内） ──

    public static AppShortcut SelectAll { get; } = new(new KeyGesture(Key.A, KeyModifiers.Control));

    public static AppShortcut ClearSelection { get; } = new(new KeyGesture(Key.Escape));

    /// <summary>用 QuickLook 预览最近点选或悬停的卡片。</summary>
    public static AppShortcut Preview { get; } = new(new KeyGesture(Key.Space));

    // ── 弹窗 ──

    /// <summary>关闭当前弹窗；弹窗打开时优先于画廊的取消选择。</summary>
    public static AppShortcut CloseDialog { get; } = new(new KeyGesture(Key.Escape));

    public static AppShortcut TogglePlay { get; } = new(new KeyGesture(Key.Space));

    public static AppShortcut Previous { get; } = new(new KeyGesture(Key.Left));

    public static AppShortcut Next { get; } = new(new KeyGesture(Key.Right));

    public static IReadOnlyList<AppShortcut> All { get; } =
    [
        OpenAlbum, Rescan, StartAction, NavigateLibrary, NavigateTasks, NavigateTools, NavigateSettings,
        SelectAll, ClearSelection, Preview, CloseDialog, TogglePlay, Previous, Next
    ];

    private static AppShortcut Page(Key digit, Key numPad) =>
        new(new KeyGesture(digit, KeyModifiers.Control), new KeyGesture(numPad, KeyModifiers.Control));
}
