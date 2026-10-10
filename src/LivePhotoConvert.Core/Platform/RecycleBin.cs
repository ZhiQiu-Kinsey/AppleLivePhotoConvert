using System.Runtime.Versioning;
using LivePhotoConvert.Core.External;
using Microsoft.VisualBasic.FileIO;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// 把文件移入系统废纸篓：Windows 回收站，macOS 废纸篓。
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// 将文件移入废纸篓；文件不存在时忽略。
    /// </summary>
    /// <exception cref="IOException">该位置没有废纸篓、用户取消或移入失败；此时文件保持原样</exception>
    /// <exception cref="PlatformNotSupportedException">当前系统没有废纸篓</exception>
    public static void Send(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SendWindows(fullPath);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            SendMac(fullPath);
            return;
        }

        throw new PlatformNotSupportedException("当前系统不支持回收站。");
    }

    /// <summary>
    /// 只有本机固定磁盘有回收站：U 盘、存储卡、光盘与网络位置（含 UNC 路径）都没有。
    /// </summary>
    internal static bool HasRecycleBin(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SendWindows(string fullPath)
    {
        // Shell 在静默模式下遇到没有回收站的位置会直接永久删除，必须事先拒绝
        if (!HasRecycleBin(fullPath))
        {
            throw new IOException($"该位置没有回收站，已保留文件：{fullPath}");
        }

        try
        {
            FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"用户取消了移入回收站操作：{fullPath}");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"移入回收站失败：{ex.Message}", ex);
        }

        if (File.Exists(fullPath))
        {
            throw new IOException($"移入回收站后文件仍然存在：{fullPath}");
        }
    }

    /// <summary>
    /// 让访达把文件移入废纸篓。路径经参数传入，避免文件名里的引号被 AppleScript 当成代码。
    /// 第一次使用时系统会询问是否允许控制访达。
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static void SendMac(string fullPath)
    {
        ProcessResult result;
        try
        {
            // 转到线程池再等待，避免从界面线程同步等待时死锁
            result = Task.Run(() => ProcessRunner.RunAsync(
                "/usr/bin/osascript",
                [
                    "-e", "on run argv",
                    "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv))",
                    "-e", "end run",
                    fullPath
                ],
                timeout: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            throw new IOException($"移入废纸篓失败：{ex.Message}", ex);
        }

        if (!result.Success || File.Exists(fullPath))
        {
            var reason = result.Success ? "文件仍然存在" : result.ErrorSummary;
            throw new IOException($"移入废纸篓失败：{reason}（{fullPath}）");
        }
    }
}
