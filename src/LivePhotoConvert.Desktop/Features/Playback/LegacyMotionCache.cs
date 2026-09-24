using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Library.Thumbnails;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 旧版播放前切出的内嵌视频缓存：不受容量管理、键会跨目录冲突，也不再有读者，启动后在后台删除。
/// </summary>
public static class LegacyMotionCache
{
    public static string DefaultDirectory { get; } = Path.Combine(TempWorkspace.Root, "motion_cache");

    public static void TryDelete() => LegacyThumbnailCache.TryDelete(DefaultDirectory);
}
