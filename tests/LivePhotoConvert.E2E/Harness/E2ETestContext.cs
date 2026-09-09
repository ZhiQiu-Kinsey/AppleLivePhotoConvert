namespace LivePhotoConvert.E2E.Harness;

/// <summary>
/// 端到端测试执行沙箱：为每个测试用例分配完全隔离的文件系统目录，并在生命周期结束时清理。
/// </summary>
public sealed class E2ETestContext : IDisposable
{
    public string RootDirectory { get; }
    public string InputDirectory { get; }
    public string OutputDirectory { get; }
    public string TempDirectory { get; }
    public string BackupDirectory { get; }

    public E2ETestContext()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoConvert.E2E", Guid.NewGuid().ToString("N"));
        InputDirectory = Path.Combine(RootDirectory, "input");
        OutputDirectory = Path.Combine(RootDirectory, "output");
        TempDirectory = Path.Combine(RootDirectory, "temp");
        BackupDirectory = Path.Combine(RootDirectory, "backup");

        Directory.CreateDirectory(InputDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    /// <summary>
    /// 在输入目录中创建测试文件
    /// </summary>
    public string CreateInputFile(string relativeFileName, byte[] content)
    {
        var fullPath = Path.Combine(InputDirectory, relativeFileName);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    /// <summary>
    /// 在输出目录中预先放置同名冲突文件
    /// </summary>
    public string CreateOutputFile(string relativeFileName, byte[] content)
    {
        var fullPath = Path.Combine(OutputDirectory, relativeFileName);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    /// <summary>
    /// 获取输出目录中的所有文件路径（相对路径）
    /// </summary>
    public IReadOnlyList<string> GetOutputFileNames()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(OutputDirectory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(OutputDirectory, p))
            .ToList();
    }

    /// <summary>
    /// 获取输入目录中的所有文件路径（相对路径）
    /// </summary>
    public IReadOnlyList<string> GetInputFileNames()
    {
        if (!Directory.Exists(InputDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(InputDirectory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(InputDirectory, p))
            .ToList();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        catch
        {
            // 忽略临时文件占用异常
        }
    }
}
