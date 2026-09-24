using System.Globalization;
using System.Xml.Linq;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Assets;

/// <summary>
/// 主题调色板的 WCAG AA 对比度：正文 4.5:1，大字（≥18px 或 ≥14px 粗体）与图标 3:1。
/// 直接解析 Styles.axaml 的 ThemeDictionaries，浅色与深色各算一遍。
/// </summary>
public class ThemeContrastTests
{
    private const double BodyText = 4.5;
    private const double LargeTextOrIcon = 3.0;

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static readonly string[] Themes = ["Light", "Dark"];

    /// <summary>视图里实际出现的前景 / 背景组合；新增组合时补在这里。</summary>
    private static readonly (string Foreground, string Background, double Minimum)[] Pairs =
    [
        // 各级正文落在窗口、卡片 / 侧栏 / 弹窗与悬停底色上
        .. Cross(["TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush"], ["CanvasBrush", "SurfaceBrush", "HoverBrush"], BodyText),
        .. Cross(["TextMutedBrush"], ["CanvasBrush", "SurfaceBrush"], BodyText),
        // 选中导航、关于页标识区等强调底色上的次要文字
        .. Cross(["TextTertiaryBrush", "TextMutedBrush", "AccentTextBrush"], ["AccentSoftBrush"], BodyText),
        .. Cross(["AccentTextBrush"], ["CanvasBrush", "SurfaceBrush", "HoverBrush"], BodyText),
        ("SuccessTextBrush", "SuccessSoftBrush", BodyText),
        ("SuccessTextBrush", "SurfaceBrush", BodyText),
        ("WarningTextBrush", "WarningSoftBrush", BodyText),
        ("WarningTextBrush", "SurfaceBrush", BodyText),
        ("DangerTextBrush", "DangerSoftBrush", BodyText),
        ("DangerTextBrush", "WarningSoftBrush", BodyText),
        ("DangerTextBrush", "SurfaceBrush", BodyText),
        // 填充按钮上的白字，含悬停后的底色
        .. Cross(["OnAccentBrush"], ["AccentBrush", "AccentPressedBrush", "DangerBrush", "DangerPressedBrush", "SuccessFillBrush", "WarningFillBrush", "CaptionCloseHoverBrush", "CaptionClosePressedBrush"], BodyText),
        .. Cross(["OnMediaBrush", "OnMediaMutedBrush"], ["MediaCanvasBrush"], BodyText),
        // 卷帘对比"瘦身后"角标
        ("OnMediaBrush", "SuccessFillBrush", BodyText),

        // 图标与状态点
        .. Cross(["AccentBrush", "VioletBrush", "SuccessBrush", "WarningBrush", "DangerBrush", "TextMutedBrush"], ["CanvasBrush", "SurfaceBrush"], LargeTextOrIcon),
        ("TextMutedBrush", "HoverBrush", LargeTextOrIcon),
        // 选中导航按钮上的页面图标
        .. Cross(["AccentBrush", "VioletBrush", "WarningBrush", "TextTertiaryBrush"], ["AccentSoftBrush"], LargeTextOrIcon),
        ("SuccessBrush", "SuccessSoftBrush", LargeTextOrIcon),
        ("WarningBrush", "WarningSoftBrush", LargeTextOrIcon),
        ("DangerBrush", "DangerSoftBrush", LargeTextOrIcon),
        .. Cross(["MediaAccentBrush", "MediaWarningBrush"], ["MediaCanvasBrush"], LargeTextOrIcon),
    ];

    [Fact]
    public void ContrastRatio_MatchesWcagReferenceValues()
    {
        Assert.Equal(21.0, ContrastRatio(Parse("#000000"), Parse("#FFFFFF")), 2);
        Assert.Equal(1.0, ContrastRatio(Parse("#2563EB"), Parse("#2563EB")), 6);
        // #767676 是白底上刚好达到 4.5:1 的灰，#777777 刚好不达标
        Assert.True(ContrastRatio(Parse("#767676"), Parse("#FFFFFF")) >= 4.5);
        Assert.True(ContrastRatio(Parse("#777777"), Parse("#FFFFFF")) < 4.5);
        // 与参数顺序无关
        Assert.Equal(ContrastRatio(Parse("#0F172A"), Parse("#F8FAFC")), ContrastRatio(Parse("#F8FAFC"), Parse("#0F172A")), 9);
    }

    [Fact]
    public void ThemePalette_KeyTokenPairs_MeetWcagAA()
    {
        var failures = new List<string>();
        foreach (var theme in Themes)
        {
            var palette = LoadPalette(theme);
            foreach (var (fg, bg, minimum) in Pairs)
            {
                Assert.True(palette.ContainsKey(fg), $"{theme} 缺少 {fg}");
                Assert.True(palette.ContainsKey(bg), $"{theme} 缺少 {bg}");
                var ratio = ContrastRatio(palette[fg], palette[bg]);
                if (ratio < minimum)
                {
                    failures.Add($"{theme}: {fg} / {bg} = {ratio:F2}:1，要求 {minimum}:1");
                }
            }
        }

        Assert.True(failures.Count == 0, "对比度不足:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void ThemeDictionaries_DefineTheSameKeysInBothThemes()
    {
        var light = LoadPalette("Light").Keys.ToHashSet();
        var dark = LoadPalette("Dark").Keys.ToHashSet();
        Assert.Empty(light.Except(dark));
        Assert.Empty(dark.Except(light));
    }

    private static IEnumerable<(string, string, double)> Cross(string[] foregrounds, string[] backgrounds, double minimum) =>
        foregrounds.SelectMany(fg => backgrounds.Select(bg => (fg, bg, minimum)));

    /// <summary>主题字典中的纯色画刷与颜色；半透明色（遮罩、阴影类）不参与文字对比度计算。</summary>
    private static Dictionary<string, (double R, double G, double B)> LoadPalette(string theme)
    {
        var doc = XDocument.Load(Path.Combine(DesktopSources.Directory, "Assets", "Styles.axaml"));
        var dictionary = doc.Descendants(Avalonia + "ResourceDictionary")
            .Single(e => (string?)e.Attribute(X + "Key") == theme);

        var palette = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);
        foreach (var brush in dictionary.Elements(Avalonia + "SolidColorBrush"))
        {
            var key = brush.Attribute(X + "Key")!.Value;
            var color = brush.Attribute("Color")!.Value;
            if (IsOpaque(color))
            {
                palette[key] = Parse(color);
            }
        }

        return palette;
    }

    private static bool IsOpaque(string hex) => hex.Length == 7 || hex.StartsWith("#FF", StringComparison.OrdinalIgnoreCase);

    private static (double R, double G, double B) Parse(string hex)
    {
        var digits = hex.TrimStart('#');
        digits = digits.Length == 8 ? digits[2..] : digits;
        Assert.Equal(6, digits.Length);
        double Channel(int i) => int.Parse(digits.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return (Channel(0), Channel(2), Channel(4));
    }

    /// <summary>WCAG 2.x 相对亮度：sRGB 通道先线性化再按人眼敏感度加权。</summary>
    private static double RelativeLuminance((double R, double G, double B) c)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static double ContrastRatio((double, double, double) a, (double, double, double) b)
    {
        var (la, lb) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
