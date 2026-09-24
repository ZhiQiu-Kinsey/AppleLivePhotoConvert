using System.Collections.Concurrent;
using System.Diagnostics;
using LivePhotoConvert.Core.External.Tools;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 定位外部可执行文件并确认其可以正常运行。
/// </summary>
public static class ToolLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>探测结果按文件路径、大小与修改时间缓存，文件被替换后自动重新探测。</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime LastWrite), bool> ProbeCache = new();

    /// <summary>
    /// 按「指定路径 → 程序目录及其 tools/、tools/&lt;工具&gt;/ 子目录 → 本地应用数据目录 → PATH」顺序查找。
    /// 指定了路径时只检查该路径。
    /// </summary>
    public static string? Find(string fileName, string? explicitPath = null, params ReadOnlySpan<string> subDirectories)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return IsValidTool(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var baseDirectories = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) }
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // ToolInstaller 把每个工具装在 tools/<不含扩展名的文件名>/ 下
        var installDirectory = Path.GetFileNameWithoutExtension(fileName);
        var candidates = new List<string>();
        foreach (var directory in baseDirectories)
        {
            candidates.Add(Path.Combine(directory!, fileName));
            candidates.Add(Path.Combine(directory!, "tools", fileName));
            candidates.Add(Path.Combine(directory!, "tools", installDirectory, fileName));
            foreach (var subDirectory in subDirectories)
            {
                candidates.Add(Path.Combine(directory!, subDirectory, fileName));
            }
        }

        candidates.Add(Path.Combine(ToolDirectories.LocalAppDataToolDirectory, fileName));
        candidates.Add(Path.Combine(ToolDirectories.LocalAppDataToolDirectory, installDirectory, fileName));
        var found = candidates.FirstOrDefault(IsValidTool) ?? FindOnPath(fileName);
        return found is null ? null : Path.GetFullPath(found);
    }

    /// <summary>
    /// 运行版本查询命令确认文件真的能执行（下载中断或被杀毒软件隔离的文件也可能存在）。
    /// </summary>
    public static bool IsValidTool(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return ProbeCache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), static key => Probe(key.Path));
    }

    private static bool Probe(string path)
    {
        var name = Path.GetFileName(path);
        var versionArgument = name.Contains("exiftool", StringComparison.OrdinalIgnoreCase) ? "-ver"
            : name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) || name.Contains("ffprobe", StringComparison.OrdinalIgnoreCase) ? "-version"
            : name.Contains("heif-enc", StringComparison.OrdinalIgnoreCase) ? "-v"
            : null;
        if (versionArgument is null)
        {
            return true;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { versionArgument },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(ProbeTimeout))
            {
                return process.ExitCode == 0;
            }

            process.Kill(entireProcessTree: true);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var range in path.AsSpan().Split(Path.PathSeparator))
        {
            var directory = path.AsSpan(range).Trim();
            if (directory.IsEmpty || directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory.ToString(), fileName);
            if (IsValidTool(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
