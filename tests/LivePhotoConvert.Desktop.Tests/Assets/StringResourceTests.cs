using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LivePhotoConvert.Core.External.Tools;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Assets;

/// <summary>字符串字典（Assets/Strings.*.axaml）是本地化唯一数据源：键集一致、无空值、无缺失与冗余、格式串可解析。</summary>
public class StringResourceTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void StringDictionaries_HaveIdenticalUniqueKeysAndNonEmptyValues()
    {
        var zh = DesktopSources.LoadEntries("zh-CN");
        var en = DesktopSources.LoadEntries("en-US");

        var zhDuplicates = zh.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var enDuplicates = en.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(zhDuplicates.Count == 0, $"zh-CN 重复键: {string.Join(", ", zhDuplicates)}");
        Assert.True(enDuplicates.Count == 0, $"en-US 重复键: {string.Join(", ", enDuplicates)}");

        var zhKeys = zh.Select(e => e.Key).ToHashSet();
        var enKeys = en.Select(e => e.Key).ToHashSet();
        var onlyZh = zhKeys.Except(enKeys).Order().ToList();
        var onlyEn = enKeys.Except(zhKeys).Order().ToList();
        Assert.True(onlyZh.Count == 0, $"仅 zh-CN 存在: {string.Join(", ", onlyZh)}");
        Assert.True(onlyEn.Count == 0, $"仅 en-US 存在: {string.Join(", ", onlyEn)}");

        var empty = zh.Concat(en).Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).Distinct().ToList();
        Assert.True(empty.Count == 0, $"空值键: {string.Join(", ", empty)}");
        Assert.True(zhKeys.Count >= 100, $"字符串键数量异常: {zhKeys.Count}");
    }

    [Fact]
    public void DynamicResourceKeys_InViews_ExistInStringDictionaries()
    {
        var strings = DesktopSources.LoadStrings("zh-CN").Keys.ToHashSet();
        var axamlFiles = DesktopSources.Files("*.axaml").Where(p => !DesktopSources.IsStringsDictionary(p)).ToList();

        // 画刷、样式、模板等由 Styles.axaml 等本地资源定义，它们不是字符串资源
        var locallyDefined = axamlFiles
            .SelectMany(p => XDocument.Load(p).Descendants())
            .Select(e => e.Attribute(X + "Key")?.Value)
            .OfType<string>()
            .ToHashSet();

        var referenced = 0;
        var missing = new SortedDictionary<string, SortedSet<string>>();
        foreach (var file in axamlFiles)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource\s+([A-Za-z0-9_]+)\s*\}"))
            {
                var key = m.Groups[1].Value;
                if (locallyDefined.Contains(key))
                {
                    continue;
                }

                referenced++;
                if (!strings.Contains(key))
                {
                    if (!missing.TryGetValue(key, out var files))
                    {
                        missing[key] = files = [];
                    }
                    files.Add(Path.GetFileName(file));
                }
            }
        }

        Assert.True(referenced > 100, $"DynamicResource 字符串引用数量异常: {referenced}");
        Assert.True(missing.Count == 0,
            "视图引用了不存在的字符串键: " + string.Join("; ", missing.Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})")));
    }

    [Fact]
    public void LocalizerKeys_UsedInCode_ExistInStringDictionaries()
    {
        var strings = DesktopSources.LoadStrings("zh-CN").Keys.ToHashSet();
        const string receiver = @"(?:(?<![\w.])_?localizer|Localizer\.Current)";
        var indexer = new Regex(receiver + @"\[(?<body>[^\]]*)\]");
        var format = new Regex(receiver + @"\.Format\(\s*""(?<key>[A-Za-z0-9_]+)""");
        var literal = new Regex(@"""(?<key>[A-Za-z0-9_]+)""");
        // 间接使用的键表：关于页鸣谢条目 new("名称", "描述键", ...)、镜像预设 ("名称键", "地址")
        var creditEntry = new Regex(@"new\(\s*[^,""]*""[^""]*""\s*,\s*""(?<key>[A-Za-z0-9_]+)""\s*,");
        var mirrorPreset = new Regex(@"\(\s*""(?<key>[A-Za-z0-9_]+)""\s*,\s*""(?:https?://[^""]*)?""\s*\)");

        var used = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string key, string file)
        {
            if (!used.TryGetValue(key, out var files))
            {
                used[key] = files = [];
            }
            files.Add(Path.GetFileName(file));
        }

        foreach (var file in DesktopSources.Files("*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in indexer.Matches(text))
            {
                foreach (Match l in literal.Matches(m.Groups["body"].Value))
                {
                    Add(l.Groups["key"].Value, file);
                }
            }

            foreach (var regex in new[] { format, creditEntry, mirrorPreset })
            {
                foreach (Match m in regex.Matches(text))
                {
                    Add(m.Groups["key"].Value, file);
                }
            }
        }

        Assert.True(used.Count > 80, $"代码中识别到的本地化键数量异常: {used.Count}");
        Assert.Contains("CreditExifToolDesc", used.Keys);
        Assert.Contains("MirrorPresetCustom", used.Keys);
        Assert.Contains("GroupTitleMonthFormat", used.Keys);

        var missing = used.Where(kv => !strings.Contains(kv.Key))
            .Select(kv => $"{kv.Key} ({string.Join(", ", kv.Value)})")
            .ToList();
        Assert.True(missing.Count == 0, "代码使用了不存在的字符串键: " + string.Join("; ", missing));
    }

    [Fact]
    public void StringDictionaries_HaveNoUnusedKeys()
    {
        // 视图里的 DynamicResource 与代码里任何与键同名的字符串字面量都算引用（间接键表同样以字面量出现）
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in DesktopSources.Files("*.axaml").Where(p => !DesktopSources.IsStringsDictionary(p)))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource)\s+([A-Za-z0-9_]+)\s*\}"))
            {
                referenced.Add(m.Groups[1].Value);
            }
        }

        foreach (var file in DesktopSources.Files("*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""([A-Za-z0-9_]+)"""))
            {
                referenced.Add(m.Groups[1].Value);
            }
        }

        // 下载源名称键写在 Core 的工具清单里，界面按清单取文案
        referenced.UnionWith(ToolManifest.Embedded.Tools.SelectMany(t => t.Packages).Select(p => p.NameKey));

        var unused = DesktopSources.LoadStrings("zh-CN").Keys.Where(k => !referenced.Contains(k)).Order().ToList();
        Assert.True(unused.Count == 0, "未被引用的字符串键: " + string.Join(", ", unused));
    }

    [Fact]
    public void ToolManifestSourceNameKeys_ExistInBothDictionaries()
    {
        var keys = ToolManifest.Embedded.Tools.SelectMany(t => t.Packages).Select(p => p.NameKey).Distinct().ToList();
        Assert.NotEmpty(keys);
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            var strings = DesktopSources.LoadStrings(language);
            var missing = keys.Where(k => !strings.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{language} 缺少下载源名称: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void FormatStrings_HaveSamePlaceholdersInBothLanguagesAndParse()
    {
        var zh = DesktopSources.LoadStrings("zh-CN");
        var en = DesktopSources.LoadStrings("en-US");
        var placeholder = new Regex(@"\{(?<index>\d+)[^{}]*\}");

        static string StripEscapedBraces(string s) => s.Replace("{{", string.Empty).Replace("}}", string.Empty);
        SortedSet<int> Indices(string value) =>
            [.. placeholder.Matches(StripEscapedBraces(value)).Select(m => int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture))];

        var mismatched = new List<string>();
        var unparsable = new List<string>();
        var formatCount = 0;
        foreach (var (key, zhValue) in zh)
        {
            if (!en.TryGetValue(key, out var enValue))
            {
                continue;
            }

            var zhIndices = Indices(zhValue);
            var enIndices = Indices(enValue);
            if (zhIndices.Count > 0 || enIndices.Count > 0)
            {
                formatCount++;
            }

            if (!zhIndices.SetEquals(enIndices))
            {
                mismatched.Add($"{key}: zh {{{string.Join(",", zhIndices)}}} / en {{{string.Join(",", enIndices)}}}");
            }

            foreach (var (language, value) in new[] { ("zh-CN", zhValue), ("en-US", enValue) })
            {
                if (value.Contains('{') || value.Contains('}'))
                {
                    try
                    {
                        CompositeFormat.Parse(value);
                    }
                    catch (FormatException)
                    {
                        unparsable.Add($"{key} ({language})");
                    }
                }
            }
        }

        Assert.True(formatCount > 20, $"格式串数量异常: {formatCount}");
        Assert.True(mismatched.Count == 0, "中英格式串占位符不一致: " + string.Join("; ", mismatched));
        Assert.True(unparsable.Count == 0, "格式串无法解析: " + string.Join("; ", unparsable));
    }

    /// <summary>零 Emoji：图标只用 FluentIcons 矢量图标。箭头（←→）属于按键说明，不算 Emoji。</summary>
    [Fact]
    public void DesktopSourcesAndStrings_ContainNoEmoji()
    {
        var found = new List<string>();
        foreach (var file in DesktopSources.Files("*.axaml").Concat(DesktopSources.Files("*.cs")))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (var rune in line.EnumerateRunes())
                {
                    if (IsEmoji(rune))
                    {
                        found.Add($"{Path.GetRelativePath(DesktopSources.Directory, file)}:{lineNumber} U+{rune.Value:X4}");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "出现 Emoji 字符:\n" + string.Join('\n', found));
    }

    /// <summary>彩色表情、杂项符号与装饰符号区段，以及把字符变成表情样式的变体选择符和零宽连接符。</summary>
    private static bool IsEmoji(Rune rune) =>
        rune.Value is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2B00 and <= 0x2BFF
            or 0xFE0F or 0x200D;
}
