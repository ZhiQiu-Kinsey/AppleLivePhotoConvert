using System.Globalization;
using LivePhotoConvert.Desktop.Infrastructure;
using LivePhotoConvert.Desktop.Models;

namespace LivePhotoConvert.Desktop.Features.Library.Gallery;

public enum GallerySort
{
    /// <summary>拍摄时间（EXIF，其次文件名中的时间，最后修改时间）。</summary>
    DateTaken,

    /// <summary>文件创建时间。</summary>
    DateCreated,

    /// <summary>文件修改时间。</summary>
    DateModified,

    Name
}

public enum GalleryGrouping
{
    Day,
    Month,
    Year,
    None
}

/// <param name="Key">分组键，跨重排稳定，用于保留折叠状态</param>
/// <param name="Period">时段起点；不分组时为 <see cref="DateTime.MinValue"/></param>
public sealed record GalleryGroup(string Key, DateTime Period, IReadOnlyList<PhotoCardItemViewModel> Cards);

/// <summary>画廊排序与分组。设置里保存的是字符串，这里负责与枚举互转。</summary>
public static class GalleryOrdering
{
    public const string NoneGroupKey = "group_none";

    public static GallerySort ParseSort(string? value) => value switch
    {
        "DateCreated" => GallerySort.DateCreated,
        "DateModified" => GallerySort.DateModified,
        "Name" => GallerySort.Name,
        _ => GallerySort.DateTaken
    };

    public static GalleryGrouping ParseGrouping(string? value) => value switch
    {
        "Month" => GalleryGrouping.Month,
        "Year" => GalleryGrouping.Year,
        "None" => GalleryGrouping.None,
        _ => GalleryGrouping.Day
    };

    /// <summary>排序与按日期分组所依据的时间（当地时间）。按名称排序时以拍摄时间分组。</summary>
    public static DateTime TimeOf(PhotoCardItemViewModel card, GallerySort sort) => sort switch
    {
        GallerySort.DateCreated => card.PhotoFile.CreationTimeUtc.ToLocalTime(),
        GallerySort.DateModified => card.PhotoFile.LastWriteTimeUtc.ToLocalTime(),
        _ => card.DateTaken
    };

    /// <summary>稳定排序：同一时间或同名时按路径排，重扫后顺序不跳动。</summary>
    public static List<PhotoCardItemViewModel> Sort(IEnumerable<PhotoCardItemViewModel> cards, GallerySort sort, bool ascending)
    {
        var list = cards.ToList();
        Comparison<PhotoCardItemViewModel> compare = sort == GallerySort.Name
            ? static (a, b) => ByName(a, b)
            : (a, b) => TimeOf(a, sort).CompareTo(TimeOf(b, sort)) is var byTime and not 0 ? byTime : ByName(a, b);
        list.Sort(ascending ? compare : (a, b) => compare(b, a));
        return list;
    }

    /// <summary>对已排序的卡片分组；组按时段排列，方向与排序一致，组内保持输入顺序。</summary>
    public static IReadOnlyList<GalleryGroup> Group(IReadOnlyList<PhotoCardItemViewModel> sorted, GalleryGrouping grouping, GallerySort sort, bool ascending)
    {
        if (grouping == GalleryGrouping.None)
        {
            return sorted.Count == 0 ? [] : [new GalleryGroup(NoneGroupKey, DateTime.MinValue, sorted)];
        }

        var groups = new Dictionary<DateTime, List<PhotoCardItemViewModel>>();
        foreach (var card in sorted)
        {
            var period = PeriodOf(TimeOf(card, sort), grouping);
            if (!groups.TryGetValue(period, out var members))
            {
                groups[period] = members = [];
            }

            members.Add(card);
        }

        var periods = groups.Keys.ToList();
        periods.Sort();
        if (!ascending)
        {
            periods.Reverse();
        }

        return [.. periods.Select(period => new GalleryGroup(KeyOf(period, grouping), period, groups[period]))];
    }

    public static string FormatTitle(ILocalizer localizer, GalleryGrouping grouping, DateTime period) => grouping switch
    {
        GalleryGrouping.Month => period.ToString(localizer["GroupTitleMonthFormat"], localizer.Culture),
        GalleryGrouping.Year => period.ToString(localizer["GroupTitleYearFormat"], localizer.Culture),
        GalleryGrouping.Day => period.ToString(localizer["DateGroupFormat"], localizer.Culture),
        _ => string.Empty
    };

    private static DateTime PeriodOf(DateTime time, GalleryGrouping grouping) => grouping switch
    {
        GalleryGrouping.Month => new DateTime(time.Year, time.Month, 1),
        GalleryGrouping.Year => new DateTime(time.Year, 1, 1),
        _ => time.Date
    };

    private static string KeyOf(DateTime period, GalleryGrouping grouping) => grouping switch
    {
        GalleryGrouping.Month => string.Create(CultureInfo.InvariantCulture, $"month_{period:yyyy_MM}"),
        GalleryGrouping.Year => string.Create(CultureInfo.InvariantCulture, $"year_{period:yyyy}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"date_{period:yyyy_MM_dd}")
    };

    private static int ByName(PhotoCardItemViewModel a, PhotoCardItemViewModel b) =>
        StringComparer.CurrentCultureIgnoreCase.Compare(a.FileName, b.FileName) is var byName and not 0
            ? byName
            : string.CompareOrdinal(a.PhotoPath, b.PhotoPath);
}
