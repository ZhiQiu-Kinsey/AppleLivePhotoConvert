using System.Diagnostics;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部可执行程序的定位与真实有效性探测
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// 按「显式指定 → 程序所在目录 → PATH」的顺序查找可执行文件（自动通过真实运行探测验证可用性）
    /// </summary>
    public static string? Find(string fileName, string? explicitPath = null, params string[] subDirectories)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) && IsValidTool(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }
        var baseDirectory = AppContext.BaseDirectory;
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        var baseDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseDirectory };
        if (!string.IsNullOrEmpty(processDir))
        {
            baseDirs.Add(processDir);
        }
        var candidates = new List<string>();
        foreach (var dir in baseDirs)
        {
            candidates.Add(Path.Combine(dir, fileName));
            candidates.AddRange(subDirectories.Select(sub => Path.Combine(dir, sub, fileName)));
        }
        candidates.Add(Path.Combine(ToolDownloader.LocalAppDataToolDirectory, fileName));
        var found = candidates.FirstOrDefault(path => File.Exists(path) && IsValidTool(path));
        return found is not null ? Path.GetFullPath(found) : FindOnPath(fileName);
    }

    /// <summary>
    /// 快速探测可执行文件是否真正能够正常启动与执行
    /// </summary>
    public static bool IsValidTool(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            var fileName = Path.GetFileName(path);
            var isExifTool = fileName.Contains("exiftool", StringComparison.OrdinalIgnoreCase);
            var isFfmpeg = fileName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase);
            if (!isExifTool && !isFfmpeg)
            {
                return true;
            }
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = isExifTool ? "-ver" : "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (proc.Start())
            {
                if (proc.WaitForExit(2000))
                {
                    return proc.ExitCode == 0;
                }
                proc.Kill();
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在 PATH 环境变量列出的目录中查找
    /// </summary>
    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (File.Exists(candidate) && IsValidTool(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}
