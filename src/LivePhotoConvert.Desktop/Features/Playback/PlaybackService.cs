using Avalonia.Threading;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Desktop.Infrastructure;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 从外部结束全部播放：退出程序、替换 FFmpeg 前必须先让播放器放开 FFmpeg 进程。
/// </summary>
public interface IPlaybackControl
{
    /// <summary>停止全部播放器，不等待进程退出。</summary>
    void StopAll();

    /// <summary>停止全部播放器并等待它们的 FFmpeg 进程退出。</summary>
    Task StopAllAsync();
}

/// <summary>
/// 播放器工厂与登记处：每个窗口（画廊、QuickLook）各自创建一个播放器，这里统一停止，并让 QuickLook 独占播放。
/// </summary>
/// <remarks>只在界面线程调用。</remarks>
public sealed class PlaybackService : IPlaybackControl
{
    private readonly Func<IFrameScheduler, ILivePhotoPlayer> _create;
    private readonly List<ILivePhotoPlayer> _players = [];
    private ILivePhotoPlayer? _exclusive;

    /// <summary>产品实现：FFmpeg 路径每次播放前按设置重新查找，安装或更换后立即生效。</summary>
    public PlaybackService(SettingsStore settings)
        : this(scheduler => new LivePhotoPlayer(scheduler, () => ResolveTools(settings.Current.FfmpegPath)))
    {
    }

    /// <param name="create">按刷新节拍创建播放器；测试可返回替身</param>
    public PlaybackService(Func<IFrameScheduler, ILivePhotoPlayer> create) => _create = create;

    /// <summary>停止全部播放时触发；持有延迟启动计时器的调用方据此一并取消。</summary>
    public event EventHandler? StoppingAll;

    public IReadOnlyList<ILivePhotoPlayer> Players => _players;

    public ILivePhotoPlayer CreatePlayer(IFrameScheduler scheduler)
    {
        Dispatcher.UIThread.VerifyAccess();
        var player = _create(scheduler);
        _players.Add(player);
        return player;
    }

    /// <summary>停止并释放播放器，等待其 FFmpeg 退出后注销。</summary>
    public async Task ReleaseAsync(ILivePhotoPlayer player)
    {
        Dispatcher.UIThread.VerifyAccess();
        ExitExclusive(player);
        try
        {
            await player.StopAsync();
        }
        finally
        {
            player.Dispose();
            _players.Remove(player);
        }
    }

    /// <summary>独占期间其它播放器全部停止，且 <see cref="CanPlay"/> 为 false。</summary>
    public void EnterExclusive(ILivePhotoPlayer owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        _exclusive = owner;
        StoppingAll?.Invoke(this, EventArgs.Empty);
        foreach (var player in _players.Where(p => !ReferenceEquals(p, owner)).ToList())
        {
            player.Stop();
        }
    }

    public void ExitExclusive(ILivePhotoPlayer owner)
    {
        if (ReferenceEquals(_exclusive, owner))
        {
            _exclusive = null;
        }
    }

    public bool CanPlay(ILivePhotoPlayer player) => _exclusive is null || ReferenceEquals(_exclusive, player);

    public void StopAll()
    {
        StoppingAll?.Invoke(this, EventArgs.Empty);
        foreach (var player in _players.ToList())
        {
            player.Stop();
        }
    }

    public async Task StopAllAsync()
    {
        StopAll();
        await Task.WhenAll(_players.ToList().Select(p => p.StopAsync()));
    }

    /// <summary>设置中的路径优先，其次程序目录与 PATH；与转换引擎的查找规则一致。</summary>
    internal static PlaybackTools? ResolveTools(string? configuredPath) =>
        ToolLocator.Find(FfmpegVideoConverter.ExecutableName, string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath) is { } ffmpeg
            ? new PlaybackTools(ffmpeg)
            : null;
}
