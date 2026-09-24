using System.Xml.Linq;

namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>
/// 直接解析桌面工程的源文件：字符串字典与视图源码的校验不依赖产品代码的加载逻辑。
/// </summary>
public static class DesktopSources
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static readonly string Directory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LivePhotoConvert.Desktop"));

    public static string StringsPath(string language) => Path.Combine(Directory, "Assets", $"Strings.{language}.axaml");

    public static bool IsStringsDictionary(string path) =>
        Path.GetFileName(path).StartsWith("Strings.", StringComparison.Ordinal)
        && Path.GetFileName(Path.GetDirectoryName(path)) == "Assets";

    public static IEnumerable<string> Files(string pattern) =>
        System.IO.Directory.EnumerateFiles(Directory, pattern, SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(Directory, p).Replace('\\', '/');
                return !rel.StartsWith("bin/", StringComparison.Ordinal) && !rel.StartsWith("obj/", StringComparison.Ordinal);
            });

    /// <summary>按 XAML 规则还原字符串值：未声明 xml:space="preserve" 时空白会被折叠。</summary>
    public static List<(string Key, string Value)> LoadEntries(string language)
    {
        var doc = XDocument.Load(StringsPath(language));
        return doc.Root!.Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .Select(e =>
            {
                var raw = e.Value;
                var preserve = (string?)e.Attribute(XNamespace.Xml + "space") == "preserve";
                return (e.Attribute(X + "Key")!.Value, preserve ? raw : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            })
            .ToList();
    }

    public static Dictionary<string, string> LoadStrings(string language) =>
        LoadEntries(language).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
}
