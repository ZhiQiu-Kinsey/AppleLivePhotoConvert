using Avalonia;
using Avalonia.Media.Imaging;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 实况视频播放器；界面只依赖本接口，测试可换成不启动 FFmpeg 的替身。约定见 <see cref="LivePhotoPlayer"/>。
/// </summary>
public interface ILivePhotoPlayer : IDisposable
{
    PlayerState State { get; }

    /// <summary>当前帧；未在播放时为 null。每换一帧就换一个实例，界面须在 <see cref="SurfaceInvalidated"/> 中重新赋值。</summary>
    Bitmap? Surface { get; }

    /// <summary>暂停时停在当前帧，恢复后从原处继续；换片或停止不清除该标记。</summary>
    bool IsPaused { get; set; }

    event EventHandler? SurfaceInvalidated;

    event EventHandler? StateChanged;

    /// <inheritdoc cref="LivePhotoPlayer.PlayAsync"/>
    Task PlayAsync(VideoSource source, PixelSize target, double scaling, PlaybackBudget budget, CancellationToken cancellationToken = default);

    /// <summary>立即停止：结束 FFmpeg、释放帧缓存，<see cref="Surface"/> 置空。</summary>
    void Stop();

    /// <summary>停止并等待 FFmpeg 进程退出。</summary>
    Task StopAsync();
}
