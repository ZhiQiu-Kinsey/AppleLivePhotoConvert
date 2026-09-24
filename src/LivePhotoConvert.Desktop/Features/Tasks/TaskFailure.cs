using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>
/// 整批无法开始的原因。只保存原始信息，文案在显示时按当前语言生成，切换语言后随之刷新。
/// </summary>
public sealed class TaskFailure
{
    private readonly string? _messageKey;

    private TaskFailure(string? messageKey, Exception? exception)
    {
        _messageKey = messageKey;
        Exception = exception;
    }

    public Exception? Exception { get; }

    /// <summary>异常原文，只用于详情展开；按资源键给出的原因没有技术细节。</summary>
    public string? Detail => Exception?.Message;

    public static TaskFailure FromKey(string messageKey) => new(messageKey, null);

    public static TaskFailure FromException(Exception exception) => new(null, exception);

    public string Describe(ILocalizer localizer) =>
        _messageKey is { } key ? localizer[key] : ErrorMessages.Describe(localizer, Exception!);
}
