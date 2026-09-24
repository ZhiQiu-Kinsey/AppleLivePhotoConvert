using System.Runtime.InteropServices;
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

    /// <summary>仅 Windows 有系统提示音；无音频设备时静默跳过，不打断主流程。</summary>
    private static void PlayCompletionSound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _ = MessageBeep(MB_ICONASTERISK);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBeep")]
    private static extern bool MessageBeep(uint uType);
}
