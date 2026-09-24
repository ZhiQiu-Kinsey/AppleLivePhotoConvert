namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>XAML 命令参数的解析：CommandParameter 可能是 {x:True} 装箱的布尔值，也可能是字符串。</summary>
public static class CommandParameters
{
    public static bool ToBool(object? value, bool fallback) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        "1" => true,
        "0" => false,
        _ => fallback
    };
}
