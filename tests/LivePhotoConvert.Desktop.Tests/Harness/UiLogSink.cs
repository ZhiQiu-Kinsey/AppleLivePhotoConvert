using Avalonia.Logging;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// 收集 Avalonia 的 Warning 及以上日志；编译绑定在运行期的失败（路径为空、类型不符等）只会写日志，不会抛异常。
/// </summary>
public sealed class UiLogSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;
    private readonly Lock _gate = new();
    private readonly List<string> _bindingErrors = [];

    public UiLogSink()
    {
        _previous = Logger.Sink;
        Logger.Sink = this;
    }

    public IReadOnlyList<string> BindingErrors
    {
        get
        {
            lock (_gate)
            {
                return [.. _bindingErrors];
            }
        }
    }

    public void AssertNoBindingErrors()
    {
        var errors = BindingErrors;
        Assert.True(errors.Count == 0, "绑定错误:\n" + string.Join("\n", errors));
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        var message = $"[{level}] {source?.GetType().Name}: {Render(messageTemplate, propertyValues)}";
        lock (_gate)
        {
            _bindingErrors.Add(message);
        }
    }

    public void Dispose() => Logger.Sink = _previous;

    /// <summary>按出现顺序把 {Name} 占位符替换为参数值。</summary>
    private static string Render(string template, object?[] values)
    {
        var result = new System.Text.StringBuilder();
        var index = 0;
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            var close = open >= 0 ? template.IndexOf('}', open) : -1;
            if (open < 0 || close < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            result.Append(template, i, open - i);
            result.Append(index < values.Length ? values[index++]?.ToString() : template[open..(close + 1)]);
            i = close + 1;
        }

        return result.ToString();
    }
}
