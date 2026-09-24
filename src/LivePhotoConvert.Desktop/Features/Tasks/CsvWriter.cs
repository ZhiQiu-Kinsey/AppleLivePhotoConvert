using System.Buffers;
using System.Text;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>
/// RFC 4180 CSV：CRLF 换行，含逗号、引号或换行的字段加引号并把引号加倍。
/// 以公式字符开头的字段按 OWASP 建议加单引号前缀，防止文件名被 Excel 当作公式执行。
/// </summary>
public static class CsvWriter
{
    private const string LineBreak = "\r\n";
    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    /// <summary>带 BOM，Excel 才会按 UTF-8 识别中文。</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().ContainsAny(NeedsQuoting)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    public static string FormatRow(IEnumerable<string?> fields) => string.Join(',', fields.Select(EscapeField));

    public static void Write(TextWriter writer, IEnumerable<IEnumerable<string?>> rows)
    {
        foreach (var row in rows)
        {
            writer.Write(FormatRow(row));
            writer.Write(LineBreak);
        }
    }

    /// <summary>先写同目录临时文件再替换，写入中途失败不会留下半个文件。</summary>
    public static void WriteFile(string path, IEnumerable<IEnumerable<string?>> rows)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full) ?? ".", $"~lpc-{Guid.NewGuid():N}.csv");
        try
        {
            using (var writer = new StreamWriter(temp, append: false, FileEncoding))
            {
                Write(writer, rows);
            }

            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            FileHelper.TryDeleteFile(temp);
        }
    }
}
