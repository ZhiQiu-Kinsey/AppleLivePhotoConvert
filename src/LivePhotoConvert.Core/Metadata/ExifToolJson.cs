using System.Globalization;
using System.Text.Json;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 解析 <c>exiftool -j -n -G1 -a</c> 的输出。键为 <c>组:标签</c>，同名标签可能出现在多个组中。
/// </summary>
internal static class ExifToolJson
{
    public static IEnumerable<MediaMetadata> ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && ToMetadata(entry) is { } metadata)
            {
                yield return metadata;
            }
        }
    }

    private static MediaMetadata? ToMetadata(JsonElement entry)
    {
        var tags = new List<Tag>();
        string? sourceFile = null;
        foreach (var property in entry.EnumerateObject())
        {
            var separator = property.Name.IndexOf(':');
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };

            if (separator < 0)
            {
                if (property.Name == "SourceFile")
                {
                    sourceFile = value;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add(new Tag(property.Name[..separator], property.Name[(separator + 1)..], value.Trim()));
            }
        }

        if (sourceFile is null)
        {
            return null;
        }

        return new MediaMetadata
        {
            Path = sourceFile,
            CaptureTime = ReadCaptureTime(tags),
            ContentIdentifier = First(tags, "ContentIdentifier"),
            Duration = ReadDouble(First(tags, "Duration")) is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null,
            IsMirrored = tags.Where(tag => tag.Name == "MatrixStructure").Any(tag => IsMirrorMatrix(tag.Value)),
            StillImageTimeUs = ReadStillImageTime(tags),
            Location = ReadLocation(tags),
            Make = First(tags, "Make", "IFD0", "Keys"),
            Model = First(tags, "Model", "IFD0", "Keys"),
            Software = First(tags, "Software", "IFD0", "Keys"),
            AppleHdrHeadroom = ReadDouble(First(tags, "HDRHeadroom")),
            AppleHdrGain = ReadDouble(First(tags, "HDRGain")),
            HasAppleGainMap = tags.Any(IsAppleGainMapAuxiliary),
            HdrGainMapVersion = ReadDouble(First(tags, "HDRGainMapVersion")) is { } version ? (long)version : null
        };
    }

    /// <summary>
    /// 优先照片的 DateTimeOriginal（配合 OffsetTimeOriginal），其次视频带时区的 Keys:CreationDate，最后是 QuickTime/EXIF 的创建时间。
    /// </summary>
    private static CaptureTime? ReadCaptureTime(List<Tag> tags)
    {
        if (TryParse(First(tags, "DateTimeOriginal", "ExifIFD", "XMP-exif"), out var original))
        {
            return original.Offset is null && CaptureTime.ParseOffset(First(tags, "OffsetTimeOriginal", "ExifIFD")) is { } offset
                ? original with { Offset = offset }
                : original;
        }

        foreach (var (name, group) in (ReadOnlySpan<(string, string?)>)
                 [("CreationDate", "Keys"), ("CreateDate", "QuickTime"), ("CreateDate", "ExifIFD"), ("CreateDate", null), ("MediaCreateDate", null)])
        {
            var text = group is null ? First(tags, name) : First(tags, name, group);
            if (TryParse(text, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Apple 把 StillImageTime 写在单样本 timed-metadata 轨道中；经 edit list 偏移后，
    /// 该轨道的 TrackDuration 等于封面帧时间加上单样本的 MediaDuration。
    /// </summary>
    private static long? ReadStillImageTime(List<Tag> tags)
    {
        foreach (var group in tags.Where(tag => tag.Name == "StillImageTime").Select(tag => tag.Group).Distinct())
        {
            var trackDuration = ReadDouble(First(tags, "TrackDuration", group));
            var mediaDuration = ReadDouble(First(tags, "MediaDuration", group));
            if (trackDuration is { } track && mediaDuration is { } media && track - media is var seconds && double.IsFinite(seconds) && seconds >= 0 && seconds < 3600)
            {
                return (long)Math.Round(seconds * 1_000_000d, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    /// <summary>
    /// 同一文件可能有多个辅助图像（深度、人像遮罩等），ExifTool 对重复标签追加序号后缀。
    /// </summary>
    private static bool IsAppleGainMapAuxiliary(Tag tag) =>
        tag.Name.StartsWith("AuxiliaryImageType", StringComparison.Ordinal)
        && tag.Value.Contains(AppleGainMapAuxiliaryType, StringComparison.OrdinalIgnoreCase);

    internal const string AppleGainMapAuxiliaryType = "urn:com:apple:photo:2020:aux:hdrgainmap";

    private static GeoLocation? ReadLocation(List<Tag> tags)
    {
        var latitude = ReadDouble(First(tags, "GPSLatitude", "Composite"));
        var longitude = ReadDouble(First(tags, "GPSLongitude", "Composite"));
        return latitude is { } lat && longitude is { } lon && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180
            ? new GeoLocation(lat, lon, ReadDouble(First(tags, "GPSAltitude", "Composite")))
            : null;
    }

    /// <summary>
    /// 3x3 矩阵按行主序输出 a b u c d v x y w，左上 2x2 子矩阵行列式为负即存在镜像。
    /// </summary>
    internal static bool IsMirrorMatrix(string text)
    {
        Span<double> values = stackalloc double[5];
        var count = 0;
        var span = text.AsSpan();
        foreach (var range in span.SplitAny(' ', '\t'))
        {
            var token = span[range];
            if (token.IsEmpty)
            {
                continue;
            }

            if (count == values.Length)
            {
                break;
            }

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[count++]))
            {
                return false;
            }
        }

        return count == values.Length && values[0] * values[4] - values[1] * values[3] < 0;
    }

    private static string? First(List<Tag> tags, string name, params ReadOnlySpan<string> preferredGroups)
    {
        foreach (var group in preferredGroups)
        {
            foreach (var tag in tags)
            {
                if (tag.Name == name && tag.Group == group)
                {
                    return tag.Value;
                }
            }
        }

        return preferredGroups.IsEmpty ? tags.FirstOrDefault(tag => tag.Name == name).Value : null;
    }

    /// <summary>
    /// QuickTime 时间为 0 表示未知；ExifTool 在 -n 下把它输出为按本机时区换算的 1904 纪元，不能当成真实拍摄时间。
    /// 1904 年各地多用地方平时，输出的偏移会截掉秒数，因此按 1 分钟容差判断。
    /// </summary>
    private static readonly DateTimeOffset QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static bool TryParse(string? text, out CaptureTime value)
    {
        value = default;
        return text is not null && CaptureTime.TryParse(text, out value)
               && (value.Offset is { } offset
                   ? (new DateTimeOffset(value.LocalTime, offset) - QuickTimeEpoch).Duration() >= TimeSpan.FromMinutes(1)
                   : value.LocalTime != QuickTimeEpoch.DateTime);
    }

    private static double? ReadDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private readonly record struct Tag(string Group, string Name, string Value);
}
