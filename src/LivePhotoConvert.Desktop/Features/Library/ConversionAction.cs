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
