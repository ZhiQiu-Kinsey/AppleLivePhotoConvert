namespace LivePhotoConvert.Core.External.Tools;

/// <summary>压缩包含有越界路径、链接或加密条目等不安全内容。</summary>
public sealed class UnsafeArchiveException(string message) : Exception(message);

/// <summary>
/// 压缩包条目路径的规范化与越界检查。
/// </summary>
internal static class ToolArchivePath
{
    /// <summary>
    /// 把条目名规范化为正斜杠分隔的相对路径；绝对路径、盘符、<c>..</c> 与非法字符一律拒绝。
    /// </summary>
    /// <exception cref="UnsafeArchiveException">路径不安全</exception>
    public static string Normalize(string entryName)
    {
        var name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':') || name.IndexOf('\0') >= 0)
        {
            throw new UnsafeArchiveException($"压缩包条目使用了绝对路径：{entryName}");
        }

        var segments = new List<string>();
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            // Windows 会静默去掉结尾的点和空格，"..." 或 ".. " 可能被解释为上级目录
            if (segment == ".." || segment.TrimEnd('.', ' ').Length == 0 || segment.IndexOfAny(InvalidSegmentChars) >= 0)
            {
                throw new UnsafeArchiveException($"压缩包条目路径越界或含非法字符：{entryName}");
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// 条目相对 <paramref name="root"/> 的路径；不在 root 下或未被 <paramref name="include"/> 选中时返回 null。
    /// </summary>
    public static string? Select(string normalizedEntry, string normalizedRoot, IReadOnlyList<string>? include)
    {
        string relative;
        if (normalizedRoot.Length == 0)
        {
            relative = normalizedEntry;
        }
        else if (normalizedEntry.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            relative = normalizedEntry[(normalizedRoot.Length + 1)..];
        }
        else
        {
            return null;
        }

        if (relative.Length == 0)
        {
            return null;
        }

        if (include is null || include.Count == 0)
        {
            return relative;
        }

        foreach (var item in include)
        {
            if (relative == item || relative.StartsWith(item + "/", StringComparison.Ordinal))
            {
                return relative;
            }
        }

        return null;
    }

    /// <summary>
    /// 把规范化后的相对路径映射到目标目录内的完整路径，并再次确认结果仍在目标目录内。
    /// </summary>
    /// <exception cref="UnsafeArchiveException">结果越出目标目录</exception>
    public static string Resolve(string destinationDirectory, string relativePath)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!full.StartsWith(rootWithSeparator, comparison))
        {
            throw new UnsafeArchiveException($"压缩包条目越出目标目录：{relativePath}");
        }

        return full;
    }

    private static readonly char[] InvalidSegmentChars = ['<', '>', '"', '|', '?', '*', .. Enumerable.Range(1, 31).Select(code => (char)code)];
}
