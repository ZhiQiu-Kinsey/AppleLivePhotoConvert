using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 设置中为各外部工具指定的路径；修改只经 <see cref="Set"/>，以便探测缓存随之失效。
/// </summary>
public sealed class ToolPathSettings(SettingsStore settings)
{
    /// <summary>某个工具的指定路径已改变。</summary>
    public event EventHandler<ToolId>? Changed;

    /// <summary>指定的路径；未指定时为 null（自动发现）。</summary>
    public string? Get(ToolId tool)
    {
        var current = settings.Current;
        var path = tool switch
        {
            ToolId.ExifTool => current.ExifToolPath,
            ToolId.Ffmpeg => current.FfmpegPath,
            _ => current.HeifEncPath
        };
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <param name="tool">工具</param>
    /// <param name="path">可执行文件路径；null 或空白表示改回自动发现</param>
    public void Set(ToolId tool, string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        if (string.Equals(Get(tool) ?? string.Empty, value, StringComparison.Ordinal))
        {
            return;
        }

        settings.Update(s =>
        {
            switch (tool)
            {
                case ToolId.ExifTool:
                    s.ExifToolPath = value;
                    break;
                case ToolId.Ffmpeg:
                    s.FfmpegPath = value;
                    break;
                default:
                    s.HeifEncPath = value;
                    break;
            }
        });
        Changed?.Invoke(this, tool);
    }

    /// <summary>路径改变时让注册表丢弃该工具的探测结果。</summary>
    public void InvalidateOnChange(IToolRegistry registry) => Changed += (_, tool) => registry.Invalidate(tool);
}
