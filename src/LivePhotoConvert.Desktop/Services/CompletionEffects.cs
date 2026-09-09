using System.Diagnostics;
using System.Runtime.InteropServices;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 任务完成后的全局效果执行器（提示音 / 自动打开输出目录）。
/// 供「偏好设置」中的 NotifyOnComplete / AutoOpenOutput 两个开关真正驱动，
/// 由 Convert / Strip 的完成回调统一调用，保证 AOT 兼容（P/Invoke + UseShellExecute）。
/// </summary>
public static class CompletionEffects
{
    /// <summary>MB_ICONASTERISK：系统信息提示音，温和不刺耳。</summary>
    private const uint MB_ICONASTERISK = 0x00000040;

    /// <summary>
    /// 播放系统提示音。仅 Windows 生效，其余平台静默跳过；
    /// 无音频设备 / 权限不足时静默降级，绝不打断主流程。
    /// </summary>
    public static void PlayCompletionSound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _ = MessageBeep(MB_ICONASTERISK);
        }
        catch
        {
            // 无音频设备或系统策略拦截时静默降级。
        }
    }

    /// <summary>
    /// 在系统资源管理器中打开输出目录（若传入的是文件路径则自动取其所在目录）。
    /// 目录不存在或系统不支持时静默降级。
    /// </summary>
    public static void OpenOutputFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string target = path;
        if (File.Exists(target))
        {
            target = Path.GetDirectoryName(target) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
            // 资源管理器不可用或系统策略拦截时静默降级。
        }
    }

    /// <summary>
    /// 任务完成后依据当前偏好设置执行提示音与自动打开输出目录。
    /// 由 Convert / Strip 的完成回调统一入口调用，避免两处重复判断。
    /// </summary>
    public static void RunOnTaskComplete(DesktopSettings settings, string? outputDirectory)
    {
        if (settings.NotifyOnComplete)
        {
            PlayCompletionSound();
        }

        if (settings.AutoOpenOutput)
        {
            OpenOutputFolder(outputDirectory);
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBeep")]
    private static extern bool MessageBeep(uint uType);
}
