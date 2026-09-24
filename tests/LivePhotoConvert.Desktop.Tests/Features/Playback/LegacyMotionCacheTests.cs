using LivePhotoConvert.Core.Pipeline;
using LivePhotoConvert.Desktop.Features.Playback;

namespace LivePhotoConvert.Desktop.Tests.Features.Playback;

public class LegacyMotionCacheTests
{
    [Fact]
    public void DefaultDirectory_IsTheOldPlayersSliceCache()
    {
        Assert.Equal(Path.Combine(TempWorkspace.Root, "motion_cache"), LegacyMotionCache.DefaultDirectory);
    }

    /// <summary>旧切片目录整体删除；同一根目录下正在使用的工作目录不受影响。</summary>
    [Fact]
    public void TryDelete_RemovesOldSlices_AndLeavesWorkspacesAlone()
    {
        var legacy = LegacyMotionCache.DefaultDirectory;
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "MVIMG_1_3000_6000.mp4"), [1, 2, 3]);
        using var workspace = new TempWorkspace();
        var inUse = workspace.NewFile(".mp4");
        File.WriteAllBytes(inUse, [4, 5, 6]);

        LegacyMotionCache.TryDelete();
        LegacyMotionCache.TryDelete();

        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(inUse));
    }
}
