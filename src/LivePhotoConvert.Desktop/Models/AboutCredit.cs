namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// “关于”页面中的一条鸣谢 / 贡献者 / 依赖条目。
/// 仅承载纯展示数据，不持有任何命令或行为，保持 AOT 友好。
/// </summary>
/// <param name="Name">展示名称（专有名词，不参与本地化）。</param>
/// <param name="Description">已本地化的描述文本。</param>
/// <param name="Url">点击时打开的外部链接。</param>
/// <param name="Badge">徽标文本（许可证名、角色或规范标识）。</param>
public sealed class AboutCredit(string name, string description, string url, string badge)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Url { get; } = url;

    public string Badge { get; } = badge;
}
