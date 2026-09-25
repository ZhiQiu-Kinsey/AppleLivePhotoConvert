using Avalonia;
using Avalonia.Controls;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Controls;

/// <summary>
/// 把文案与 <see cref="AppShortcut"/> 合成工具提示（"重新扫描（F5）" / "Rescan (F5)"）。
/// 文案在控件自身上解析 DynamicResource / 绑定，切换语言时提示随之更新；按键文本只来自快捷键定义。
/// </summary>
public sealed class ShortcutTip : AvaloniaObject
{
    public static readonly AttachedProperty<string?> LabelProperty =
        AvaloniaProperty.RegisterAttached<ShortcutTip, Control, string?>("Label");

    public static readonly AttachedProperty<AppShortcut?> ShortcutProperty =
        AvaloniaProperty.RegisterAttached<ShortcutTip, Control, AppShortcut?>("Shortcut");

    static ShortcutTip()
    {
        LabelProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
        ShortcutProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    private ShortcutTip()
    {
    }

    public static string? GetLabel(Control control) => control.GetValue(LabelProperty);

    public static void SetLabel(Control control, string? value) => control.SetValue(LabelProperty, value);

    public static AppShortcut? GetShortcut(Control control) => control.GetValue(ShortcutProperty);

    public static void SetShortcut(Control control, AppShortcut? value) => control.SetValue(ShortcutProperty, value);

    /// <summary>提示文本的唯一合成规则；没有文案时只显示按键，中文文案配全角括号。</summary>
    /// <remarks>按文案本身判断而不查当前语言：文案随 DynamicResource 切换时括号随之切换，无需额外订阅。</remarks>
    public static string? Compose(string? label, AppShortcut? shortcut) => shortcut is null
        ? label
        : string.IsNullOrWhiteSpace(label) ? shortcut.DisplayText
        : label.AsSpan().ContainsAnyInRange('\u4E00', '\u9FFF') ? $"{label}（{shortcut.DisplayText}）"
        : $"{label} ({shortcut.DisplayText})";

    private static void Apply(Control control) =>
        ToolTip.SetTip(control, Compose(GetLabel(control), GetShortcut(control)));
}
