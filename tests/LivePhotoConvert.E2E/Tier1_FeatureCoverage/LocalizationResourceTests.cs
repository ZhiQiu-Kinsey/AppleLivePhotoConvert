using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.E2E.Harness;

namespace LivePhotoConvert.E2E.Tier1_FeatureCoverage;

/// <summary>
/// 本地化单一数据源（Assets/Strings.*.axaml）的一致性与 <see cref="Localizer"/> 行为。
/// 资源校验直接解析 XAML 与源码文本，不依赖产品代码的加载逻辑。
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public class LocalizationResourceTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Xml = XNamespace.Xml;

    private static readonly string DesktopDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop"));

    private static string StringsPath(string language) => Path.Combine(DesktopDir, "Assets", $"Strings.{language}.axaml");

    private static bool IsStringsDictionary(string path) =>
        Path.GetFileName(path).StartsWith("Strings.", StringComparison.Ordinal)
        && Path.GetFileName(Path.GetDirectoryName(path)) == "Assets";

    private static IEnumerable<string> DesktopSources(string pattern) =>
        Directory.EnumerateFiles(DesktopDir, pattern, SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(DesktopDir, p).Replace('\\', '/');
                return !rel.StartsWith("bin/", StringComparison.Ordinal) && !rel.StartsWith("obj/", StringComparison.Ordinal);
            });

    /// <summary>按 XAML 规则还原字符串值：未声明 xml:space="preserve" 时空白会被折叠。</summary>
    private static List<(string Key, string Value)> LoadEntries(string language)
    {
        var doc = XDocument.Load(StringsPath(language));
        return doc.Root!.Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .Select(e =>
            {
                var raw = e.Value;
                var preserve = (string?)e.Attribute(Xml + "space") == "preserve";
                return (e.Attribute(X + "Key")!.Value, preserve ? raw : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            })
            .ToList();
    }

    private static Dictionary<string, string> LoadStrings(string language) =>
        LoadEntries(language).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    [Fact]
    public void StringDictionaries_HaveIdenticalUniqueKeysAndNonEmptyValues()
    {
        var zh = LoadEntries("zh-CN");
        var en = LoadEntries("en-US");

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
        var strings = LoadStrings("zh-CN").Keys.ToHashSet();
        var axamlFiles = DesktopSources("*.axaml").Where(p => !IsStringsDictionary(p)).ToList();

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
        var strings = LoadStrings("zh-CN").Keys.ToHashSet();
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

        foreach (var file in DesktopSources("*.cs"))
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
        foreach (var file in DesktopSources("*.axaml").Where(p => !IsStringsDictionary(p)))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource)\s+([A-Za-z0-9_]+)\s*\}"))
            {
                referenced.Add(m.Groups[1].Value);
            }
        }

        foreach (var file in DesktopSources("*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""([A-Za-z0-9_]+)"""))
            {
                referenced.Add(m.Groups[1].Value);
            }
        }

        var unused = LoadStrings("zh-CN").Keys.Where(k => !referenced.Contains(k)).Order().ToList();
        Assert.True(unused.Count == 0, "未被引用的字符串键: " + string.Join(", ", unused));
    }

    [Fact]
    public void FormatStrings_HaveSamePlaceholdersInBothLanguagesAndParse()
    {
        var zh = LoadStrings("zh-CN");
        var en = LoadStrings("en-US");
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

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void Localizer_ResolvesEveryXamlKeyFromCompiledDictionary(string language)
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();
        localizer.SetLanguage(language);

        var mismatched = LoadEntries(language)
            .Where(e => localizer[e.Key] != e.Value)
            .Select(e => $"{e.Key}: '{localizer[e.Key]}' != '{e.Value}'")
            .ToList();

        Assert.True(mismatched.Count == 0, "运行时文案与 XAML 不一致: " + string.Join("; ", mismatched));
    }

    [Fact]
    public void Localizer_DefaultsToChinese()
    {
        var localizer = new Localizer();

        Assert.Equal(Localizer.Chinese, localizer.Language);
        Assert.Equal("zh-CN", localizer.Culture.Name);
        Assert.Equal("图库", localizer["NavLibrary"]);
    }

    [Theory]
    [InlineData("en-US", "en-US", "Library")]
    [InlineData("en", "en-US", "Library")]
    [InlineData("EN", "en-US", "Library")]
    [InlineData("EN-US", "en-US", "Library")]
    [InlineData("zh-CN", "zh-CN", "图库")]
    [InlineData("zh", "zh-CN", "图库")]
    [InlineData("ZH-CN", "zh-CN", "图库")]
    [InlineData("fr-FR", "zh-CN", "图库")]
    [InlineData("", "zh-CN", "图库")]
    [InlineData(null, "zh-CN", "图库")]
    public void Localizer_SetLanguage_NormalizesCodeSwitchesCultureAndRaisesEvent(string? code, string expected, string expectedNavLibrary)
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();
        var raised = 0;
        localizer.LanguageChanged += (sender, _) =>
        {
            Assert.Same(localizer, sender);
            // 事件触发时新语言必须已经生效
            Assert.Equal(expected, localizer.Language);
            raised++;
        };

        localizer.SetLanguage(code!);

        Assert.Equal(1, raised);
        Assert.Equal(expected, localizer.Language);
        Assert.Equal(expected, localizer.Culture.Name);
        Assert.Equal(expected, CultureInfo.CurrentCulture.Name);
        Assert.Equal(expected, CultureInfo.CurrentUICulture.Name);
        Assert.Equal(expected, CultureInfo.DefaultThreadCurrentCulture?.Name);
        Assert.Equal(expected, CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal(expectedNavLibrary, localizer["NavLibrary"]);
    }

    [Fact]
    public void Localizer_Format_UsesCurrentLanguageAndRefreshesAfterSwitch()
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();

        localizer.SetLanguage("zh-CN");
        Assert.Equal("12,345 项已扫描", localizer.Format("FunnelTotalFormat", 12345));

        localizer.SetLanguage("en-US");
        Assert.Equal("Scanned: 12,345", localizer.Format("FunnelTotalFormat", 12345));
        Assert.Equal("May 2025", new DateTime(2025, 5, 10).ToString(localizer["GroupTitleMonthFormat"], localizer.Culture));

        localizer.SetLanguage("zh-CN");
        Assert.Equal("12,345 项已扫描", localizer.Format("FunnelTotalFormat", 12345));
        Assert.Equal("2025年5月", new DateTime(2025, 5, 10).ToString(localizer["GroupTitleMonthFormat"], localizer.Culture));
        Assert.Equal("2025年", new DateTime(2025, 5, 10).ToString(localizer["GroupTitleYearFormat"], localizer.Culture));
    }

    [Fact]
    public void Localizer_Format_ArgumentMismatchOrNoArguments_ReturnsTemplate()
    {
        var localizer = new Localizer();
        var template = localizer["DeleteAffectedFormat"];

        Assert.Contains("{1}", template);
        Assert.Equal(template, localizer.Format("DeleteAffectedFormat", 3));
        Assert.Equal(template, localizer.Format("DeleteAffectedFormat"));
    }
}
