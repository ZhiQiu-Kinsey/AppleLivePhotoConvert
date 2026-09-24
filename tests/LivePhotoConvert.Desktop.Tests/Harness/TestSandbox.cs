namespace LivePhotoConvert.Desktop.Tests.Harness;

/// <summary>每个用例独占的输入/输出目录，结束时删除。</summary>
public sealed class TestSandbox : IDisposable
{
    public TestSandbox()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert.Desktop.Tests", Guid.NewGuid().ToString("N"));
        InputDirectory = Path.Combine(RootDirectory, "input");
        OutputDirectory = Path.Combine(RootDirectory, "output");
        Directory.CreateDirectory(InputDirectory);
        Directory.CreateDirectory(OutputDirectory);
    }

    public string RootDirectory { get; }

    public string InputDirectory { get; }

    public string OutputDirectory { get; }

    public string CreateInputFile(string relativeFileName, byte[] content)
    {
        var fullPath = Path.Combine(InputDirectory, relativeFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    /// <summary>输入目录中的全部文件（相对路径）。</summary>
    public IReadOnlyList<string> GetInputFileNames() =>
        [.. Directory.EnumerateFiles(InputDirectory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(InputDirectory, p))];

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件仍被占用时留给系统临时目录清理
        }
    }
}
