namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部工具缺失。界面按 <see cref="ToolName"/> 本地化提示，不直接显示异常消息。
/// </summary>
public sealed class ToolNotFoundException(string toolName)
    : FileNotFoundException($"未找到 {toolName}，请在「依赖引擎」页面下载或指定路径。", toolName)
{
    public string ToolName { get; } = toolName;
}
