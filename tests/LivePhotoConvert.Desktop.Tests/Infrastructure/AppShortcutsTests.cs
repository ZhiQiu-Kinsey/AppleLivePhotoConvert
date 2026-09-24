using Avalonia.Input;
using LivePhotoConvert.Desktop.Controls;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

/// <summary>快捷键定义：匹配规则、显示文本，以及文案中不手写按键。</summary>
public class AppShortcutsTests
{
    [Theory]
    [InlineData(Key.O, KeyModifiers.Control, true)]
    [InlineData(Key.O, KeyModifiers.Meta, true)]
    [InlineData(Key.O, KeyModifiers.None, false)]
    [InlineData(Key.O, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(Key.P, KeyModifiers.Control, false)]
    public void OpenAlbum_MatchesCtrlOrCommandWithExactModifiers(Key key, KeyModifiers modifiers, bool expected) =>
        Assert.Equal(expected, AppShortcuts.OpenAlbum.Matches(key, modifiers));

    [Fact]
    public void PageShortcuts_AcceptDigitRowAndNumPad()
    {
        Assert.True(AppShortcuts.NavigateTools.Matches(Key.D3, KeyModifiers.Control));
        Assert.True(AppShortcuts.NavigateTools.Matches(Key.NumPad3, KeyModifiers.Meta));
        Assert.False(AppShortcuts.NavigateTools.Matches(Key.D3, KeyModifiers.None));
        Assert.Equal([AppPage.Library, AppPage.Tasks, AppPage.Tools, AppPage.Settings], AppShortcuts.Pages.Select(p => p.Page));
    }

    [Fact]
    public void DisplayText_IsDerivedFromTheFirstGesture()
    {
        Assert.Equal("Ctrl+O", AppShortcuts.OpenAlbum.DisplayText);
        Assert.Equal("F5", AppShortcuts.Rescan.DisplayText);
        Assert.Equal("Enter", AppShortcuts.StartAction.DisplayText);
        Assert.Equal("Ctrl+A", AppShortcuts.SelectAll.DisplayText);
        Assert.Equal("Esc", AppShortcuts.ClearSelection.DisplayText);
        Assert.Equal("Space", AppShortcuts.Preview.DisplayText);
        Assert.Equal("←", AppShortcuts.Previous.DisplayText);
        Assert.Equal("→", AppShortcuts.Next.DisplayText);
        Assert.Equal(["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4"], AppShortcuts.Pages.Select(p => p.Shortcut.DisplayText));
    }

    /// <summary>主窗口的全局快捷键之间不能互相遮挡。</summary>
    [Fact]
    public void GlobalShortcuts_DoNotShareGestures()
    {
        AppShortcut[] global = [AppShortcuts.OpenAlbum, AppShortcuts.Rescan, AppShortcuts.StartAction, .. AppShortcuts.Pages.Select(p => p.Shortcut)];
        var gestures = global.SelectMany(s => s.Gestures).Select(g => (g.Key, g.KeyModifiers)).ToList();
        Assert.Equal(gestures.Count, gestures.Distinct().Count());
    }

    [Fact]
    public void Compose_AppendsKeyToLabel()
    {
        Assert.Equal("重新扫描 (F5)", ShortcutTip.Compose("重新扫描", AppShortcuts.Rescan));
        Assert.Equal("F5", ShortcutTip.Compose(null, AppShortcuts.Rescan));
        Assert.Equal("关闭", ShortcutTip.Compose("关闭", null));
    }

    /// <summary>按键只写在快捷键定义里：文案若再写一份，改键时提示就会与实际绑定不一致。</summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void StringResources_DoNotSpellOutShortcutKeys(string language)
    {
        var offenders = DesktopSources.LoadStrings(language)
            .Where(kv => kv.Value.Contains("Ctrl+", StringComparison.OrdinalIgnoreCase) ||
                         AppShortcuts.All.Any(s => kv.Value.Contains($"({s.DisplayText})", StringComparison.Ordinal)) ||
                         kv.Value.Contains('←') || kv.Value.Contains('→'))
            .Select(kv => $"{kv.Key} = {kv.Value}")
            .ToList();
        Assert.True(offenders.Count == 0, "文案中手写了快捷键：\n" + string.Join("\n", offenders));
    }
}
