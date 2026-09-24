namespace LivePhotoConvert.Core.External.Tools;

/// <summary>工具安装目录的选择。</summary>
public static class ToolDirectories
{
    /// <summary>本地应用数据下的工具目录，程序目录不可写时使用。</summary>
    public static string LocalAppDataToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "LivePhotoConvert",
        "tools");

    /// <summary>
    /// 优先程序目录下的 tools（便携部署），无写权限时退到本地应用数据目录。
    /// </summary>
    public static string GetWritableToolDirectory()
    {
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processDirectory) && TryEnsureWritable(Path.Combine(processDirectory, "tools")) is { } processTools)
        {
            return processTools;
        }

        if (TryEnsureWritable(Path.Combine(AppContext.BaseDirectory, "tools")) is { } baseTools)
        {
            return baseTools;
        }

        var local = LocalAppDataToolDirectory;
        Directory.CreateDirectory(local);
        return local;
    }

    /// <summary>可能存放已安装工具的根目录（去重，不检查存在性）。</summary>
    public static IEnumerable<string> CandidateToolRoots()
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var roots = new List<string>();
        foreach (var baseDirectory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                roots.Add(Path.Combine(baseDirectory, "tools"));
            }
        }

        roots.Add(LocalAppDataToolDirectory);
        return roots.Select(Path.GetFullPath).Distinct(comparer);
    }

    private static string? TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
