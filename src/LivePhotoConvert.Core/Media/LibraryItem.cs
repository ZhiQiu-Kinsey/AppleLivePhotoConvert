using LivePhotoConvert.Core.Pairing;

namespace LivePhotoConvert.Core.Media;

/// <summary>
/// 图库条目的类型。
/// </summary>
public enum LibraryItemKind
{
    /// <summary>苹果实况对：照片 + 同主干的独立视频。</summary>
    ApplePair,

    /// <summary>安卓动态照片：视频内嵌在照片文件中。</summary>
    MotionPhoto,

    /// <summary>普通照片。</summary>
    Still
}

/// <summary>
/// 拍摄时间的来源，按可信度从高到低。
/// </summary>
public enum CaptureTimeSource
{
    Exif,
    FileName,
    LastWrite
}

/// <summary>
/// 扫描时一并取得的文件信息，后续统计体积与排序不再访问磁盘。
/// </summary>
public sealed record LibraryFile(string Path, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

/// <summary>
/// 条目的视频数据位置：实况对为整段视频文件，动态照片为照片内的 [Offset, Offset + Length)，可直接映射到 FFmpeg 的 subfile 协议。
/// </summary>
public sealed record VideoSource(string Path, long Offset, long Length, bool IsEmbedded);

/// <summary>
/// 一次扫描产出的图库条目：每张照片（同目录同主干的多种格式合为一张）只对应一个条目。
/// </summary>
public sealed record LibraryItem(LibraryItemKind Kind, LibraryFile Photo)
{
    /// <summary>实况对选定的视频：第一个通过配对校验的候选；都未通过时为优先级最高的候选。</summary>
    public LibraryFile? Video { get; init; }

    /// <summary>动态照片内嵌视频的位置。</summary>
    public EmbeddedVideo? Embedded { get; init; }

    /// <summary>同主干的全部照片×视频候选，按格式优先级排列；合成时逐个校验择优。</summary>
    public IReadOnlyList<MediaPair> PairCandidates { get; init; } = [];

    /// <summary>头部信息；文件无法识别时为 <c>null</c>。</summary>
    public ImageHeader? Header { get; init; }

    /// <summary>带 Ultra HDR 增益图（转码 HEIC 会丢失 HDR）。</summary>
    public bool HasGainMap { get; init; }

    /// <summary>拍摄的当地时间。</summary>
    public DateTime CaptureTimeLocal { get; init; }

    public CaptureTimeSource CaptureTimeSource { get; init; }

    /// <summary>
    /// 实况对按 <see cref="PairValidator"/> 校验的结论与依据，与合成阶段的判定一致；未通过时列出全部候选的原因。
    /// </summary>
    public PairValidationResult? PairValidation { get; init; }

    /// <summary>选定的照片与视频拍摄时间之差；任一侧缺少拍摄时间时为 <c>null</c>。</summary>
    public TimeSpan? PairTimeDelta { get; init; }

    /// <summary>配对校验未通过，合成前需要人工裁决，否则会被跳过。</summary>
    public bool RequiresPairReview => PairValidation is { IsAccepted: false };

    /// <summary>源文件总字节数；动态照片的视频已包含在照片文件内。</summary>
    public long SourceBytes => Photo.Length + (Video?.Length ?? 0);

    public VideoSource? VideoSource => Kind switch
    {
        LibraryItemKind.ApplePair when Video is { } video => new VideoSource(video.Path, 0, video.Length, IsEmbedded: false),
        LibraryItemKind.MotionPhoto when Embedded is { } embedded => new VideoSource(Photo.Path, embedded.Offset, embedded.Length, IsEmbedded: true),
        _ => null
    };
}

/// <summary>
/// 扫描结果。
/// </summary>
/// <param name="Items">条目，按照片路径排序</param>
/// <param name="TotalFiles">枚举到的文件总数（不含本程序的暂存与备份文件）</param>
/// <param name="InaccessibleEntries">因权限等原因无法读取而跳过的目录数</param>
public sealed record LibraryScanResult(IReadOnlyList<LibraryItem> Items, int TotalFiles, int InaccessibleEntries);

/// <summary>
/// 扫描进度：先枚举文件，再逐条分析。
/// </summary>
/// <param name="FilesFound">已枚举的文件数</param>
/// <param name="ItemsAnalyzed">已分析的条目数</param>
/// <param name="ItemsTotal">待分析的条目总数；枚举阶段为 0</param>
public readonly record struct LibraryScanProgress(int FilesFound, int ItemsAnalyzed, int ItemsTotal);
