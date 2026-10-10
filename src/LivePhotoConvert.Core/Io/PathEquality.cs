namespace LivePhotoConvert.Core.Io;

/// <summary>
/// 用户文件路径的比较。Windows 与 macOS 的默认卷不区分大小写，Linux 区分。
/// </summary>
public static class PathEquality
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsLinux()
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison { get; } = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;
}
