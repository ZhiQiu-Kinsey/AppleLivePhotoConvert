namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 一次播放可占用的像素内存上限，包含帧缓存与两张显示位图。
/// </summary>
public readonly record struct PlaybackBudget(long Bytes)
{
    public static PlaybackBudget Hover { get; } = new(96L * 1024 * 1024);

    public static PlaybackBudget QuickLook { get; } = new(256L * 1024 * 1024);
}
