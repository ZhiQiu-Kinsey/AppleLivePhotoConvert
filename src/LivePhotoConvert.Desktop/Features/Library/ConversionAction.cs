namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>图库对选中项执行的动作。</summary>
public enum ConversionAction
{
    /// <summary>苹果实况对合成安卓动态照片。</summary>
    ToAndroid,

    /// <summary>安卓动态照片还原为苹果实况对。</summary>
    ToApple,

    /// <summary>安卓动态照片解包为封面与独立视频。</summary>
    Extract,

    /// <summary>剥离内嵌视频并转码 HEIC。</summary>
    Strip
}

/// <summary>图库扫描模式，对应 <see cref="Services.AlbumScanner.ScanDirectoryAsync"/> 的方向参数。</summary>
public static class ScanModes
{
    /// <summary>只列出苹果实况对。</summary>
    public const int ApplePairs = 0;

    /// <summary>只列出安卓动态照片。</summary>
    public const int MotionPhotos = 1;

    /// <summary>苹果实况对与安卓动态照片都列出。</summary>
    public const int All = 2;

    /// <summary>
    /// 动作对应的扫描模式。瘦身同时处理苹果实况对与安卓动态照片，扫描器中只有模式 2 两者都列出。
    /// </summary>
    public static int For(ConversionAction action) => action switch
    {
        ConversionAction.ToAndroid => ApplePairs,
        ConversionAction.ToApple => MotionPhotos,
        _ => All
    };
}
