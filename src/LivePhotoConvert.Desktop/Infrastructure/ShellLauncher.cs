using System.Diagnostics;
using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>在系统文件管理器或浏览器中打开目标；失败时返回 false 并已写日志，由调用方提示用户。</summary>
public interface IShellLauncher
{
    /// <summary>打开目录；传入文件路径时打开其所在目录。</summary>
    bool OpenFolder(string? path);

    /// <summary>打开文件所在目录并尽可能选中该文件。</summary>
    bool RevealFile(string? path);

    bool OpenUri(string? uri);
}

public sealed class ShellLauncher : IShellLauncher
{
    public bool OpenFolder(string? path)
    {
        var folder = ResolveFolder(path);
        return folder is not null && Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }, "打开目录");
    }

    public bool RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return OpenFolder(path);
        }

        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            // explorer 的 /select 参数要求与路径拼成一个参数
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            psi.ArgumentList.Add($"/select,{full}");
            return Start(psi, "定位文件");
        }

        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(full);
            return Start(psi, "定位文件");
        }

        // Linux 各文件管理器没有统一的“选中”参数，退化为打开所在目录
        return OpenFolder(Path.GetDirectoryName(full));
    }

    public bool OpenUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        return Start(new ProcessStartInfo { FileName = parsed.AbsoluteUri, UseShellExecute = true }, "打开链接");
    }

    private static string? ResolveFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                full = Path.GetDirectoryName(full) ?? full;
            }

            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Start(ProcessStartInfo psi, string context)
    {
        try
        {
            using var process = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log(ex, $"{context}: {psi.FileName}");
            return false;
        }
    }
}
