using System.Runtime.Versioning;
using Microsoft.VisualBasic.FileIO;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// 跨平台与 Windows 安全回收站文件删除服务（基于 .NET BCL 原生 FileSystem 实现）
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// 将指定文件安全移入系统回收站（如果文件不存在则直接忽略）
    /// </summary>
    /// <param name="filePath">目标文件路径</param>
    /// <exception cref="IOException">移入回收站操作失败或未成功移走</exception>
    [SupportedOSPlatform("windows")]
    public static void Send(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            // 使用 .NET BCL 官方标准回收站操作（无需手动声明 Win32 结构体，支持长路径与撤销操作）
            FileSystem.DeleteFile(
                fullPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"用户取消了移入回收站操作：{fullPath}");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"移入回收站失败：{ex.Message}", ex);
        }

        // 兜底校验
        if (File.Exists(fullPath))
        {
            throw new IOException($"移入回收站后文件仍然存在：{fullPath}");
        }
    }
}

