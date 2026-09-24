using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 按显示刷新节拍回调；回调参数为单调递增的时间戳。测试用可控时钟替换。
/// </summary>
public interface IFrameScheduler
{
    /// <summary>在下一次刷新时调用一次 <paramref name="callback"/>（UI 线程）。</summary>
    void RequestFrame(Action<TimeSpan> callback);
}

/// <summary>跟随窗口渲染节拍，窗口不渲染时自然暂停，不空转。</summary>
public sealed class TopLevelFrameScheduler(TopLevel topLevel) : IFrameScheduler
{
    public void RequestFrame(Action<TimeSpan> callback) => topLevel.RequestAnimationFrame(callback);
}
