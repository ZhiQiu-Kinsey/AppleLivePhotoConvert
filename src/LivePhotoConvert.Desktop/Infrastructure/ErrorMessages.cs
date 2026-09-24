using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 把异常转成界面文案：Core 的异常消息不随界面语言变化，能归类为原因码的改用资源文案。
/// </summary>
public static class ErrorMessages
{
    public static string Describe(ILocalizer localizer, Exception exception) =>
        OutcomeCause.FromException(exception) is { Reason: not OutcomeReason.Unexpected } cause
            ? OutcomeTexts.Describe(localizer, cause)
            : exception.Message;
}
