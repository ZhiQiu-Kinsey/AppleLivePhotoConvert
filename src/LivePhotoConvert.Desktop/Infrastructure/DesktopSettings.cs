using System.Text.Json.Serialization;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;

namespace LivePhotoConvert.Desktop.Infrastructure;

/// <summary>
/// 桌面端持久化配置。字段变更需同步提升 <see cref="SettingsStore.CurrentSchemaVersion"/> 并补迁移。
/// </summary>
public sealed class DesktopSettings
{
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;

    // 全局偏好
    public string Theme { get; set; } = "Light"; // Light, Dark, Auto
    public string Language { get; set; } = "zh"; // zh, en
    public int Concurrency { get; set; } = ConversionDefaults.Parallelism;
    public bool NotifyOnComplete { get; set; } = true;
    public bool AutoOpenOutput { get; set; }
    public bool AutoCleanTemp { get; set; } = true;

    // 转换参数（检查器）
    [JsonConverter(typeof(JsonStringEnumConverter<ConversionAction>))]
    public ConversionAction Action { get; set; } = ConversionAction.ToAndroid;

    public int NamingFormat { get; set; } = 1; // 0=保持原名, 1=日期+原名, 2=纯时间戳
    public int SourceAction { get; set; } // 0=保留原片, 1=移入备份子目录, 2=移入回收站, 3=永久删除
    public bool KeepSubfolderHierarchy { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<ConflictPolicy>))]
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.AppendIndex;

    public int HeicQuality { get; set; } = ConversionDefaults.HeicQuality;
    public string OutputDirectory { get; set; } = string.Empty;

    // 空间瘦身
    public bool InPlaceStrip { get; set; }
    public string StripOutputDirectory { get; set; } = string.Empty;
    public bool StripConvertToHeic { get; set; } = true;

    // 图库：唯一的相册目录，所有动作共用
    public string LastScanDirectory { get; set; } = string.Empty;
    /// <summary>文件中写成 null 时同样回退默认值，调用方不必判空。</summary>
    public GalleryPreferences Gallery { get; set => field = value ?? new(); } = new();

    // 依赖引擎
    public string CustomMirrorUrl { get; set; } = "https://ghproxy.net/";
    public string ExifToolPath { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;
    public string HeifEncPath { get; set; } = string.Empty;

    /// <summary>主窗口最近一次的常规（非最大化）位置与大小；从未记录时为 null。</summary>
    public WindowPlacement? Window { get; set; }
}

/// <summary>图库视图偏好；取值与工具栏命令参数一致，无法识别的值在读取时回退默认。</summary>
public sealed class GalleryPreferences
{
    public string SortMode { get; set; } = "DateTaken";
    public bool SortAscending { get; set; }
    public string Grouping { get; set; } = "Date";
    public string Scale { get; set; } = "Medium";
    public string Crop { get; set; } = "Natural";
}

/// <summary>窗口位置为物理像素，宽高为与缩放无关的逻辑单位（与 Avalonia 的 Position / Width 一致）。</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
