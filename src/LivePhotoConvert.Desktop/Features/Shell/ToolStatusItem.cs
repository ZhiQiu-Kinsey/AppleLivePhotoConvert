using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoConvert.Desktop.Features.Shell;

/// <summary>侧栏依赖状态的三档：缺失不可用、可用但需要注意（如建议升级、缺少 HDR 能力）、就绪。</summary>
public enum ToolHealth
{
    Missing,
    Attention,
    Ready
}

/// <summary>侧栏里一个依赖引擎的状态行。</summary>
public sealed partial class ToolStatusItem(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(NeedsAttention), nameof(IsMissing))]
    private ToolHealth _health;

    public bool IsReady => Health == ToolHealth.Ready;

    public bool NeedsAttention => Health == ToolHealth.Attention;

    public bool IsMissing => Health == ToolHealth.Missing;

    /// <summary>找不到工具时"需要注意"无从谈起，缺失优先。</summary>
    public static ToolHealth Evaluate(bool isReady, bool needsAttention) =>
        !isReady ? ToolHealth.Missing : needsAttention ? ToolHealth.Attention : ToolHealth.Ready;

    /// <summary>整体状态取最差的一项。</summary>
    public static ToolHealth Worst(IEnumerable<ToolStatusItem> items) =>
        items.Select(i => i.Health).DefaultIfEmpty(ToolHealth.Ready).Min();
}
