using System.Globalization;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Infrastructure;

/// <summary><see cref="Localizer"/> 的语言切换、区域设置与格式化。</summary>
[Collection(ProcessStateCollection.Name)]
public class LocalizerTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void Localizer_ResolvesEveryXamlKeyFromCompiledDictionary(string language)
    {
        using var _ = new CultureScope();
        var localizer = new Localizer();
        localizer.SetLanguage(language);

        var mismatched = DesktopSources.LoadEntries(language)
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
