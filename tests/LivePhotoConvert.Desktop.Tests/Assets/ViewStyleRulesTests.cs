using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Assets;

/// <summary>视图与样式只用主题 token 着色，文字不小于 11px。</summary>
public partial class ViewStyleRulesTests
{
    private const double MinimumTextSize = 11;

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>尚未按这两条规则整理的目录（相对桌面工程），整理后从这里移除。</summary>
    private static readonly string[] PendingDirectories = ["Controls/", "Features/Library/", "Features/Tools/"];

    [Fact]
    public void Views_UseThemeTokensInsteadOfLiteralColors()
    {
        var violations = new List<string>();
        foreach (var (file, doc) in CheckedViews())
        {
            foreach (var element in doc.Descendants())
            {
                // 主题字典是 token 的定义处
                if (element.Ancestors().Any(a => a.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
                {
                    continue;
                }

                foreach (var (property, value) in ColorAssignments(element))
                {
                    // 阴影是与主题无关的半透明黑，BoxShadows 也无法引用画刷资源
                    if (property == "BoxShadow")
                    {
                        continue;
                    }

                    if (LiteralColor().IsMatch(value) || (IsColorProperty(property) && NamedColor().IsMatch(value)))
                    {
                        violations.Add($"{file}: <{element.Name.LocalName}> {property}=\"{value}\"");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "写死的颜色:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void TextElements_AreAtLeastElevenPixels()
    {
        var violations = new List<string>();
        foreach (var (file, doc) in CheckedViews())
        {
            foreach (var element in doc.Descendants())
            {
                if (element.Name.LocalName == "Setter")
                {
                    if ((string?)element.Attribute("Property") == "FontSize" && !TargetsIcon(element) && IsTooSmall((string?)element.Attribute("Value")))
                    {
                        violations.Add($"{file}: Style \"{SelectorOf(element)}\" FontSize={element.Attribute("Value")!.Value}");
                    }
                }
                else if (!IsIcon(element.Name.LocalName) && IsTooSmall((string?)element.Attribute("FontSize")))
                {
                    violations.Add($"{file}: <{element.Name.LocalName}> FontSize={element.Attribute("FontSize")!.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0, "小于 11px 的文字:\n" + string.Join('\n', violations));
    }

    private static IEnumerable<(string File, XDocument Doc)> CheckedViews() =>
        DesktopSources.Files("*.axaml")
            .Where(p => !DesktopSources.IsStringsDictionary(p))
            .Select(p => Path.GetRelativePath(DesktopSources.Directory, p).Replace('\\', '/'))
            .Where(rel => !PendingDirectories.Any(d => rel.StartsWith(d, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .Select(rel => (rel, XDocument.Load(Path.Combine(DesktopSources.Directory, rel))));

    private static IEnumerable<(string Property, string Value)> ColorAssignments(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName is not "Property" and not "Value")
            {
                yield return (attribute.Name.LocalName, attribute.Value);
            }
        }

        if (element.Name.LocalName == "Setter" && element.Attribute("Property") is { } property && element.Attribute("Value") is { } value)
        {
            yield return (property.Value, value.Value);
        }
    }

    private static bool IsColorProperty(string property) =>
        property is "Background" or "Foreground" or "Fill" or "Stroke" or "Color"
        || property.EndsWith("Brush", StringComparison.Ordinal);

    private static bool IsTooSmall(string? value) =>
        value is not null
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
        && size < MinimumTextSize;

    private static bool IsIcon(string elementName) => elementName is "SymbolIcon" or "PathIcon";

    private static bool TargetsIcon(XElement setter) => SelectorOf(setter).TrimEnd().EndsWith("SymbolIcon", StringComparison.Ordinal);

    private static string SelectorOf(XElement setter) =>
        (string?)setter.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Style")?.Attribute("Selector") ?? string.Empty;

    [GeneratedRegex(@"^#[0-9A-Fa-f]{3,8}$")]
    private static partial Regex LiteralColor();

    [GeneratedRegex(@"^(White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange)$")]
    private static partial Regex NamedColor();
}
