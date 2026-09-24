namespace LivePhotoConvert.Core.Pairing;

/// <summary>
/// 一组候选的照片与视频。
/// </summary>
/// <param name="PhotoPath">照片路径</param>
/// <param name="VideoPath">视频路径</param>
/// <param name="IsContentIdentifierMatched">是否由 ContentIdentifier 精确配对（确定的 1:1 关系，不再与同名候选竞争）</param>
public sealed record MediaPair(string PhotoPath, string VideoPath, bool IsContentIdentifierMatched = false)
{
    /// <summary>文件名主干（不含扩展名）。</summary>
    public string Name => Path.GetFileNameWithoutExtension(PhotoPath);

    /// <summary>同名候选的分组键：同一目录下的同一文件名主干。</summary>
    public string GroupKey => GroupKeyOf(PhotoPath);

    internal static string GroupKeyOf(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
}

/// <summary>
/// 文件列表的配对结果。
/// </summary>
public sealed record PairingResult
{
    /// <summary>
    /// 候选配对。ContentIdentifier 精确配对的是确定结果；同名配对按扩展名优先级排列，
    /// 合成时每组只取第一个通过校验的候选。
    /// </summary>
    public required IReadOnlyList<MediaPair> Pairs { get; init; }

    public IReadOnlyList<string> PhotosWithoutVideo { get; init; } = [];

    public IReadOnlyList<string> VideosWithoutPhoto { get; init; } = [];
}

/// <summary>
/// 忽略路径大小写（Windows）与分隔符差异比较配对。
/// </summary>
public sealed class MediaPairPathEqualityComparer : IEqualityComparer<MediaPair>
{
    public static readonly MediaPairPathEqualityComparer Instance = new();

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool Equals(MediaPair? x, MediaPair? y) =>
        ReferenceEquals(x, y)
        || (x is not null && y is not null
            && PathComparer.Equals(Normalize(x.PhotoPath), Normalize(y.PhotoPath))
            && PathComparer.Equals(Normalize(x.VideoPath), Normalize(y.VideoPath)));

    public int GetHashCode(MediaPair obj) =>
        HashCode.Combine(PathComparer.GetHashCode(Normalize(obj.PhotoPath)), PathComparer.GetHashCode(Normalize(obj.VideoPath)));

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
