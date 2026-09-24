using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>收集窗口中显示给用户的文案，检查语言切换后是否残留另一种语言或退化成资源键名。</summary>
public static partial class UiTexts
{
    private static readonly Lazy<HashSet<string>> Keys = new(() => [.. DesktopSources.LoadStrings("en-US").Keys]);

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}]")]
    private static partial Regex Cjk();

    public static bool ContainsChinese(string text) => Cjk().IsMatch(text);

    public static IReadOnlyList<(string Text, string Source)> Collect(ShellSession session)
    {
        var texts = new List<(string, string)>();
        void Add(object? value, StyledElement source)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                texts.Add((s, Describe(source)));
            }
        }

        Add(session.Window.Title, session.Window);
        foreach (var element in session.AllElements())
        {
            switch (element)
            {
                case TextBlock block:
                    Add(block.Text ?? block.Inlines?.Text, block);
                    break;
                case TextBox box:
                    Add(box.Text, box);
                    Add(box.PlaceholderText, box);
                    break;
                case ToggleSwitch toggle:
                    Add(toggle.OnContent, toggle);
                    Add(toggle.OffContent, toggle);
                    break;
                case HeaderedContentControl headered:
                    Add(headered.Header, headered);
                    Add(headered.Content, headered);
                    break;
                case ContentControl content:
                    Add(content.Content, content);
                    break;
                case ContentPresenter presenter:
                    Add(presenter.Content, presenter);
                    break;
            }

            if (element is Control control)
            {
                Add(ToolTip.GetTip(control), control);
            }
        }

        return texts;
    }

    /// <summary>缺失的键在 Localizer 中退化为键名，在 DynamicResource 中表现为空白或键名。</summary>
    public static void AssertNoResourceKeys(ShellSession session, string context)
    {
        var leaked = Collect(session).Where(t => Keys.Value.Contains(t.Text.Trim())).Select(Format).Distinct().ToList();
        Assert.True(leaked.Count == 0, $"{context}：界面显示了资源键名\n" + string.Join("\n", leaked));
    }

    /// <summary>英文界面不得出现中文；完整路径属于用户数据，不在检查范围内。</summary>
    public static void AssertNoChinese(ShellSession session, string context)
    {
        var chinese = Collect(session)
            .Where(t => ContainsChinese(t.Text) && !Path.IsPathFullyQualified(t.Text))
            .Select(Format)
            .Distinct()
            .ToList();
        Assert.True(chinese.Count == 0, $"{context}：英文界面残留中文\n" + string.Join("\n", chinese));
    }

    private static string Format((string Text, string Source) t) => $"  {t.Source}: \"{t.Text}\"";

    /// <summary>所在视图 + 直接父元素 + 元素本身，足以在失败信息里定位到 XAML。</summary>
    private static string Describe(StyledElement element)
    {
        var view = element.Parent;
        while (view is not null and not UserControl and not TopLevel)
        {
            view = view.Parent;
        }

        var parent = element.Parent is { } p ? Name(p) + " > " : string.Empty;
        return $"{(view is null ? "?" : view.GetType().Name)}: {parent}{Name(element)}";
    }

    private static string Name(StyledElement e) => string.IsNullOrEmpty(e.Name) ? e.GetType().Name : $"{e.GetType().Name}#{e.Name}";
}
