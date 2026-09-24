using System.Runtime.Versioning;
using Microsoft.VisualBasic.FileIO;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// Windows 回收站。
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// 将文件移入回收站；文件不存在时忽略。
    /// </summary>
    /// <exception cref="IOException">该位置没有回收站、用户取消或移入失败；此时文件保持原样</exception>
    [SupportedOSPlatform("windows")]
    public static void Send(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return;
        }

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
}
