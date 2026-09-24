using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Tests.Harness;

namespace LivePhotoConvert.Desktop.Tests.Features.Library;

/// <summary>点击检查器的动作磁贴后，参数区只显示该动作用得到的部分。</summary>
[Collection(ProcessStateCollection.Name)]
public class InspectorViewSmokeTests
{
    /// <summary>各参数区的标题键。</summary>
    private static readonly string[] Sections =
        ["NamingFormatTitle", "InPlaceStripTitle", "HeicQualityTitle", "SourceActionTitle", "InspectorOutputTitle", "SpaceEstimateTitle"];

    [AvaloniaTheory]
    [InlineData(ConversionAction.ToAndroid, true, false, "NamingFormatTitle", "SourceActionTitle", "InspectorOutputTitle", "OutputFolder")]
    [InlineData(ConversionAction.ToApple, true, false, "HeicQualityTitle", "SourceActionTitle", "InspectorOutputTitle", "OutputFolder")]
    [InlineData(ConversionAction.Extract, true, false, "SourceActionTitle", "InspectorOutputTitle", "OutputFolder")]
    [InlineData(ConversionAction.Strip, true, false, "InPlaceStripTitle", "HeicQualityTitle", "InspectorOutputTitle", "SpaceEstimateTitle", "StripFolder")]
    [InlineData(ConversionAction.Strip, false, false, "InPlaceStripTitle", "InspectorOutputTitle", "SpaceEstimateTitle", "StripFolder")]
    [InlineData(ConversionAction.Strip, true, true, "InPlaceStripTitle", "HeicQualityTitle", "SpaceEstimateTitle")]
    public void ActionTile_ShowsOnlyMatchingParameterSections(ConversionAction action, bool stripToHeic, bool inPlace, params string[] expected)
    {
        using var session = new ShellSession(settings: s =>
        {
            // 先停在另一个动作上，确保点击磁贴确实发生了切换
            s.Action = action == ConversionAction.Extract ? ConversionAction.ToAndroid : ConversionAction.Extract;
            s.StripConvertToHeic = stripToHeic;
            s.InPlaceStrip = inPlace;
        });
        var inspector = session.Shell.Inspector;
        var view = session.Descendants<InspectorView>().Single();

        var tile = view.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, inspector.SetActionCommand) && (string?)b.CommandParameter == action.ToString());
        session.Click(tile);

        Assert.Equal(action, inspector.Action);
        Assert.Equal(action, session.Host.Settings.Current.Action);
        Assert.True(tile.Classes.Contains("active"), "当前动作的磁贴没有高亮");

        foreach (var key in Sections)
        {
            var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == session.Localizer[key]);
            Assert.True(expected.Contains(key) == title.IsEffectivelyVisible,
                $"{action}（转 HEIC={stripToHeic}，就地={inPlace}）：{key} 可见性应为 {expected.Contains(key)}");
        }

        Assert.Equal(expected.Contains("OutputFolder"), FolderButton(view, inspector.SelectOutputFolderCommand).IsEffectivelyVisible);
        Assert.Equal(expected.Contains("StripFolder"), FolderButton(view, inspector.SelectStripFolderCommand).IsEffectivelyVisible);
        session.Log.AssertNoBindingErrors();
    }

    [AvaloniaFact]
    public void OptionComboBoxes_ShowSavedChoiceAndFollowLanguageWithoutLosingIt()
    {
        using var session = new ShellSession(settings: s =>
        {
            s.Action = ConversionAction.ToAndroid;
            s.NamingFormat = 2;
            s.SourceAction = 3;
        });
        var inspector = session.Shell.Inspector;
        var view = session.Descendants<InspectorView>().Single();
        var combos = view.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(2, combos.Count);

        void AssertShows(string namingKey, string sourceKey)
        {
            Assert.Equal(session.Localizer[namingKey], SelectionText(combos[0]));
            Assert.Equal(session.Localizer[sourceKey], SelectionText(combos[1]));
        }

        AssertShows("NamingFormatCleanShort", "SourceActionDeleteShort");

        session.Shell.Settings.SetLanguageCommand.Execute("en");
        session.Pump();
        AssertShows("NamingFormatCleanShort", "SourceActionDeleteShort");
        Assert.Equal("Delete permanently", SelectionText(combos[1]), ignoreCase: true);

        session.Shell.Settings.SetLanguageCommand.Execute("zh");
        session.Pump();
        AssertShows("NamingFormatCleanShort", "SourceActionDeleteShort");

        // 语言切换不能经双向绑定把选择改掉或写进设置
        Assert.Equal((2, 3), (inspector.NamingFormat, inspector.SourceAction));
        Assert.Equal((2, 3), (session.Host.Settings.Current.NamingFormat, session.Host.Settings.Current.SourceAction));
        session.Log.AssertNoBindingErrors();
    }

    /// <summary>选中框（下拉未展开时）显示的文字。</summary>
    private static string? SelectionText(ComboBox combo) =>
        combo.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).SingleOrDefault(t => !string.IsNullOrEmpty(t));

    private static Button FolderButton(InspectorView view, System.Windows.Input.ICommand command) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));
}
