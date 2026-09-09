namespace LivePhotoConvert.Desktop.Models;

/// <summary>
/// 桌面端持久化配置模型
/// </summary>
public sealed class DesktopSettings
{
    public string Theme { get; set; } = "Light"; // Light, Dark, Auto
    public string Language { get; set; } = "zh"; // zh, en
    public int Concurrency { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public int NamingFormat { get; set; } = 1; // 0=保持原名, 1=日期+原名, 2=纯时间戳
    public int SourceAction { get; set; } = 0; // 0=保留原片, 1=已拆分子目录, 2=移入回收站, 3=物理删除
    public bool KeepSubfolderHierarchy { get; set; } = true;
    public bool OverwriteSameName { get; set; } = false;
    public int HeicQuality { get; set; } = 90;

    // 全局行为开关（偏好设置页）
    public bool NotifyOnComplete { get; set; } = true;   // 转换完成后播放提示音
    public bool AutoOpenOutput { get; set; } = false;    // 转换完成后自动打开输出目录
    public bool AutoCleanTemp { get; set; } = true;      // 自动清理转换产生的临时文件

    public bool InPlaceStrip { get; set; } = false;
    public string StripOutputDirectory { get; set; } = string.Empty;

    public string CustomMirrorUrl { get; set; } = "https://ghproxy.net/";
    public bool AutoDownloadDependencies { get; set; } = true;

    public string LastScanDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;

    public string ExifToolPath { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;
    public string HeifEncPath { get; set; } = string.Empty;
}
