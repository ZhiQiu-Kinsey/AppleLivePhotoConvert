namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 测试用临时目录，释放时整体删除。
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Root = Directory.CreateTempSubdirectory("lpc-test-").FullName;

    public string Root { get; }

    /// <summary>创建文件；名称可包含子目录，返回规范化路径（Windows 上 "/" 转为 "\"），与被测代码返回的路径可直接比较。</summary>
    public string CreateFile(string name, byte[]? content = null)
    {
        var path = Path.GetFullPath(Path.Combine(Root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? []);
        return path;
    }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>目录下（含子目录）除暂存文件外的全部文件名，便于断言输出。</summary>
    public string[] FileNames(string? subdirectory = null)
    {
        var dir = subdirectory is null ? Root : Combine(subdirectory);
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 残留的临时目录不影响测试结果
        }
    }
}
