using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 把异常转成界面文案：Core 的异常消息不随界面语言变化，已知类型改用资源文案。
/// </summary>
public static class ErrorMessages
{
    public static string Describe(ILocalizer localizer, Exception exception) => exception switch
    {
        ToolNotFoundException missing => localizer.Format("ToolMissingFormat", missing.ToolName),
        _ => exception.Message
    };
}
