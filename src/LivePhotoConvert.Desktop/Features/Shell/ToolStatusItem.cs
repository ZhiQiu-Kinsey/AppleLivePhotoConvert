using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoConvert.Desktop.Features.Shell;

/// <summary>
/// 侧栏依赖状态：缺失不可用、可用但需要注意（如建议升级、缺少 HDR 能力）、尚在探测、就绪。
/// 取值顺序即严重程度，整体状态取最小值：已确认的问题优先于"检测中"。
/// </summary>
public enum ToolHealth
{
    Missing,
    Attention,
    Probing,
    Ready
}

/// <summary>侧栏里一个依赖引擎的状态行。</summary>
public sealed partial class ToolStatusItem(string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>启动探测完成前为"检测中"，不能先显示成缺失。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(NeedsAttention), nameof(IsMissing), nameof(IsProbing))]
    private ToolHealth _health = ToolHealth.Probing;

    public bool IsReady => Health == ToolHealth.Ready;

    public bool NeedsAttention => Health == ToolHealth.Attention;

    public bool IsMissing => Health == ToolHealth.Missing;

    public bool IsProbing => Health == ToolHealth.Probing;

    /// <summary>找不到工具时"需要注意"无从谈起，缺失优先。</summary>
    public static ToolHealth Evaluate(bool isReady, bool needsAttention) =>
        !isReady ? ToolHealth.Missing : needsAttention ? ToolHealth.Attention : ToolHealth.Ready;

    /// <summary>尚无探测结果时不下结论。</summary>
    public static ToolHealth Evaluate(bool isProbing, bool isReady, bool needsAttention) =>
        isProbing ? ToolHealth.Probing : Evaluate(isReady, needsAttention);

    /// <summary>整体状态取最差的一项。</summary>
    public static ToolHealth Worst(IEnumerable<ToolStatusItem> items) =>
        items.Select(i => i.Health).DefaultIfEmpty(ToolHealth.Ready).Min();
}
