using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 下拉框选项，文案随语言切换原地更新。
/// ComboBox 的选中框保存的是选中项内容的快照：选项内容直接用 DynamicResource 字符串时，切换语言后选中框仍显示旧语言；
/// 改为绑定本对象的 <see cref="Text"/>，选中框经 ItemTemplate 绑定到同一个对象，文案变化会随属性通知刷新。
/// </summary>
public sealed partial class LocalizedOption(string key, bool isDanger = false) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>危险选项（例如永久删除原片）用警示色显示。</summary>
    public bool IsDanger { get; } = isDanger;

    [ObservableProperty]
    private string _text = string.Empty;

    public void Refresh(ILocalizer localizer) => Text = localizer[Key];
}
