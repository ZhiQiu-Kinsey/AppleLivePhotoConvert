using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Services;

/// <summary>
/// 任务完成后按偏好设置播放提示音、打开输出目录。
/// </summary>
public sealed class CompletionEffects(SettingsStore settings, IShellLauncher shell)
{
    /// <summary>MB_ICONASTERISK：系统信息提示音。</summary>
    private const uint MB_ICONASTERISK = 0x00000040;

    public void RunOnTaskComplete(string? outputDirectory)
    {
        var current = settings.Current;
        if (current.NotifyOnComplete)
        {
            PlayCompletionSound();
        }

        if (current.AutoOpenOutput)
        {
            shell.OpenFolder(outputDirectory);
        }
    }

    /// <summary>Windows 用信息提示音，macOS 用系统提示音；无音频设备时静默跳过，不打断主流程。</summary>
    private static void PlayCompletionSound()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = MessageBeep(MB_ICONASTERISK);
            }
            else if (OperatingSystem.IsMacOS())
            {
                NSBeep();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBeep")]
    private static extern bool MessageBeep(uint uType);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "NSBeep")]
    [SupportedOSPlatform("macos")]
    private static extern void NSBeep();
}
