using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 输出位置与重名处理。
/// </summary>
/// <param name="Directory">输出根目录</param>
public sealed record OutputOptions(string Directory)
{
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.AppendIndex;

    /// <summary>设置后按源文件相对该目录的子路径在输出目录中重建层级。</summary>
    public string? PreserveHierarchyFrom { get; init; }

    public string DirectoryFor(string sourcePath)
    {
        if (PreserveHierarchyFrom is null)
        {
            return Directory;
        }

        var relative = Path.GetRelativePath(PreserveHierarchyFrom, Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
        return relative == "." || IsOutside(relative) || Path.IsPathRooted(relative)
            ? Directory
            : Path.Combine(Directory, relative);
    }

    /// <summary>只看首段是否为 <c>..</c>：名为 <c>..abc</c> 的子目录仍在根目录之内。</summary>
    private static bool IsOutside(string relative) =>
        relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                         || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
}

public static class ConversionDefaults
{
    /// <summary>CPU 核心数的一半，限制在 1~4：解码与转码同时进行时内存占用较高。</summary>
    public static int Parallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public const int HeicQuality = 90;

    /// <summary>超过此时间的暂存文件视为异常退出的残留。</summary>
    public static readonly TimeSpan StaleStagingAge = TimeSpan.FromDays(1);
}
