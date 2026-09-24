using LivePhotoConvert.Core.Pairing;
using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Core.Services;
using LivePhotoConvert.Desktop.Features.Library;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Tasks;

/// <summary>外部工具路径；为空时由 Core 在程序目录与 PATH 中查找。</summary>
public sealed record ToolPaths(string? ExifTool, string? Ffmpeg, string? HeifEnc)
{
    public static ToolPaths Auto { get; } = new(null, null, null);

    public static ToolPaths From(DesktopSettings settings) =>
        new(NullIfBlank(settings.ExifToolPath), NullIfBlank(settings.FfmpegPath), NullIfBlank(settings.HeifEncPath));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>动作参数；各动作只读取与自己相关的字段。</summary>
public sealed record ConversionOptions
{
    /// <summary>输出位置；仅瘦身允许为 null，表示就地替换。</summary>
    public OutputOptions? Output { get; init; }

    /// <summary>就地瘦身时的源位置，用于完成后打开目录。</summary>
    public string? InPlaceLocation { get; init; }

    public MergeNamingFormat Naming { get; init; }

    public SourceFileAction SourceAction { get; init; }

    public int HeicQuality { get; init; } = ConversionDefaults.HeicQuality;

    public bool ConvertToHeic { get; init; } = true;

    /// <summary>合成时保留 iPhone HDR 增益图（Ultra HDR 封面）。</summary>
    public bool PreserveHdr { get; init; } = true;
}

/// <summary>启动时固定的输入：合成读取配对，其余动作读取文件列表。</summary>
public sealed record ConversionInputs
{
    public IReadOnlyList<MediaPair> Pairs { get; init; } = [];

    /// <summary>用户人工确认的配对，同时也包含在 <see cref="Pairs"/> 中。</summary>
    public IReadOnlyList<MediaPair> ForceAccepted { get; init; } = [];

    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>
/// 提交给任务中心的一次转换；运行期间界面上的任何改动都不会影响它。
/// </summary>
public sealed record ConversionJob(ConversionAction Action, ConversionOptions Options, ConversionInputs Inputs)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public ToolPaths Tools { get; init; } = ToolPaths.Auto;

    public int Parallelism { get; init; } = ConversionDefaults.Parallelism;

    /// <summary>处理项数。合成输入含同主干的全部候选，每组只产出一项，与合成引擎的分组一致。</summary>
    public int ItemCount => Action == ConversionAction.ToAndroid
        ? Inputs.Pairs.Count(p => p.IsContentIdentifierMatched)
          + Inputs.Pairs.Where(p => !p.IsContentIdentifierMatched).Select(p => p.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        : Inputs.Files.Count;

    /// <summary>完成后打开的位置。</summary>
    public string ResultLocation => Options.Output?.Directory ?? Options.InPlaceLocation ?? string.Empty;

    /// <summary>
    /// 只保留主源文件在 <paramref name="sources"/> 中的输入，动作与参数不变；合成以照片路径作为主源文件。
    /// </summary>
    public ConversionJob RetryWith(IEnumerable<string> sources)
    {
        var keep = new HashSet<string>(sources.Select(Normalize), PathComparer);
        bool Kept(string path) => keep.Contains(Normalize(path));

        return this with
        {
            Inputs = new ConversionInputs
            {
                Pairs = [.. Inputs.Pairs.Where(p => Kept(p.PhotoPath))],
                ForceAccepted = [.. Inputs.ForceAccepted.Where(p => Kept(p.PhotoPath))],
                Files = [.. Inputs.Files.Where(Kept)]
            }
        };
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
