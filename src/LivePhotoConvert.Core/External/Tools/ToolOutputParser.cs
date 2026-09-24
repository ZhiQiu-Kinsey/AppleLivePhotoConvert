using System.Text.RegularExpressions;

namespace LivePhotoConvert.Core.External.Tools;

/// <summary>
/// 解析外部工具的版本与能力输出。只依赖稳定的列布局，不依赖说明文字。
/// </summary>
internal static partial class ToolOutputParser
{
    /// <summary>提取工具报告的版本文本（如 13.59、n7.0-7-gd38bf5e08e-20240407、1.23.1）。</summary>
    public static string? ParseVersionText(ToolId tool, string output)
    {
        foreach (var rawLine in output.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (tool == ToolId.Ffmpeg)
            {
                // "ffmpeg version 6.1.1-3ubuntu5 Copyright ..."
                const string Marker = "ffmpeg version ";
                if (!line.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = line[Marker.Length..].TrimStart();
                var end = rest.IndexOf(' ');
                return (end < 0 ? rest : rest[..end]).ToString();
            }

            // heif-enc 旧版本会输出 "libheif: 1.17.6" 之类带前缀的行
            var colon = line.LastIndexOf(':');
            var value = (colon >= 0 ? line[(colon + 1)..] : line).Trim();
            var space = value.IndexOf(' ');
            return (space < 0 ? value : value[..space]).ToString();
        }

        return null;
    }

    /// <summary>
    /// 从版本文本中取出数字版本；master 构建（N-12345-g…）等没有发行版本号的返回 null。
    /// </summary>
    public static Version? ParseVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var match = NumericVersion().Match(versionText.Trim());
        return match.Success && Version.TryParse(match.Groups["v"].Value, out var version) ? version : null;
    }

    /// <summary><c>ffmpeg -filters</c>：每行 "标志 名称 输入->输出 说明"。</summary>
    public static HashSet<string> ParseFilterNames(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in output.AsSpan().EnumerateLines())
        {
            var tokens = rawLine.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && tokens[2].Contains("->", StringComparison.Ordinal))
            {
                names.Add(tokens[1]);
            }
        }

        return names;
    }

    /// <summary><c>ffmpeg -encoders</c>：分隔线 "------" 之后每行 "标志 名称 说明"。</summary>
    public static HashSet<string> ParseEncoderNames(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var afterSeparator = false;
        foreach (var rawLine in output.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (!afterSeparator)
            {
                afterSeparator = line.StartsWith("------", StringComparison.Ordinal);
                continue;
            }

            var tokens = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                names.Add(tokens[1]);
            }
        }

        return names;
    }

    /// <summary><c>ffmpeg -h encoder=xxx</c> 中 "Supported pixel formats:" 行列出的像素格式。</summary>
    public static IReadOnlyList<string> ParsePixelFormats(string output)
    {
        const string Marker = "Supported pixel formats:";
        foreach (var rawLine in output.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
            {
                return line[Marker.Length..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return [];
    }

    [GeneratedRegex(@"^[nNvV]?(?<v>\d+(?:\.\d+){1,3})")]
    private static partial Regex NumericVersion();
}
