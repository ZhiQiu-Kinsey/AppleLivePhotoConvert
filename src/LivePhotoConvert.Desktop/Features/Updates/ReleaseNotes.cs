using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LivePhotoConvert.Desktop.Features.Updates;

public enum ReleaseNoteKind
{
    Heading,
    Bullet,
    Paragraph
}

/// <summary>更新说明中的一段：标题、列表项（<paramref name="Level"/> 为缩进层级）或正文。</summary>
public sealed record ReleaseNoteBlock(ReleaseNoteKind Kind, string Text, int Level = 0)
{
    public bool IsHeading => Kind == ReleaseNoteKind.Heading;

    public bool IsBullet => Kind == ReleaseNoteKind.Bullet;

    public bool IsParagraph => Kind == ReleaseNoteKind.Paragraph;

    /// <summary>列表项按层级缩进。</summary>
    public Avalonia.Thickness Indent => new(Level * 14, 0, 0, 0);
}

/// <summary>
/// 把 CHANGELOG 段落（Markdown 子集）拆成纯文本块：标题、列表与段落，去掉行内标记与表情符号。
/// 表格、代码块等按普通文本显示；只求可读，不追求完整渲染。
/// </summary>
public static partial class ReleaseNotes
{
    [GeneratedRegex(@"^(?<indent>\s*)(?:[-*+]|\d+[.)])\s+(?<text>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}(?<marks>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"!?\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|__|`)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\s*([-*_]\s*){3,}$")]
    private static partial Regex Rule();

    public static IReadOnlyList<ReleaseNoteBlock> Parse(string? markdown)
    {
        var blocks = new List<ReleaseNoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var paragraph = new StringBuilder();
        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new ReleaseNoteBlock(ReleaseNoteKind.Paragraph, paragraph.ToString()));
                paragraph.Clear();
            }
        }

        var inFence = false;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                AddText(blocks, ReleaseNoteKind.Paragraph, raw, 0);
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw) || Rule().IsMatch(raw))
            {
                FlushParagraph();
                continue;
            }

            if (Heading().Match(raw) is { Success: true } heading)
            {
                FlushParagraph();
                AddText(blocks, ReleaseNoteKind.Heading, heading.Groups["text"].Value, 0);
                continue;
            }

            if (ListItem().Match(raw) is { Success: true } item)
            {
                FlushParagraph();
                var indent = item.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
                AddText(blocks, ReleaseNoteKind.Bullet, item.Groups["text"].Value, Math.Min(indent / 2, 3));
                continue;
            }

            // 列表项的续行并入上一项；其余相邻行合成一个段落
            var text = Clean(raw);
            if (paragraph.Length == 0 && blocks.Count > 0 && blocks[^1].IsBullet && raw.StartsWith(' '))
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + text };
                continue;
            }

            if (text.Length > 0)
            {
                if (paragraph.Length > 0)
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(text);
            }
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>去掉链接地址、强调与代码标记、表情符号，合并多余空白。</summary>
    public static string Clean(string text)
    {
        var withoutLinks = Link().Replace(text, m => m.Groups["text"].Value);
        var plain = Emphasis().Replace(withoutLinks, string.Empty);
        var builder = new StringBuilder(plain.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(plain);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (!IsPictograph(element))
            {
                builder.Append(element);
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AddText(List<ReleaseNoteBlock> blocks, ReleaseNoteKind kind, string text, int level)
    {
        var clean = Clean(text);
        if (clean.Length > 0)
        {
            blocks.Add(new ReleaseNoteBlock(kind, clean, level));
        }
    }

    /// <summary>表情符号（含变体选择符、零宽连接的组合）在界面字体中常显示为方框，零 Emoji 策略下直接去掉。</summary>
    private static bool IsPictograph(string element)
    {
        var rune = element.EnumerateRunes().First();
        return rune.Value is >= 0x1F000 and <= 0x1FAFF
            || rune.Value is >= 0x2600 and <= 0x27BF
            || rune.Value is 0xFE0F or 0x200D
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol && rune.Value > 0xFFFF;
    }
}
