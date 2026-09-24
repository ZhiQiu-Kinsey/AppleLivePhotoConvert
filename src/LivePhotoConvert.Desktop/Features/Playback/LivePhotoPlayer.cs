using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>播放所需的外部工具。</summary>
/// <param name="FfmpegPath">ffmpeg 可执行文件</param>
/// <param name="Filters">已知可用的滤镜；null 时首次播放 HDR 视频前探测并缓存</param>
public sealed record PlaybackTools(string FfmpegPath, IReadOnlySet<string>? Filters = null);

/// <summary>
/// 实况视频播放器：FFmpeg 解码为 BGRA 帧，按 PTS 由刷新节拍驱动，输出一张可绑定的位图。
/// 一个实例同一时刻只播放一个视频，新播放会先结束上一个的 FFmpeg。
/// </summary>
/// <remarks>
/// 除 <see cref="StopAsync"/> 的等待外，所有成员都只能在 UI 线程调用。
/// <see cref="Surface"/> 每呈现一帧就换成另一张位图（双缓冲），随后触发 <see cref="SurfaceInvalidated"/>；
/// 停止后 <see cref="Surface"/> 变为 null，旧位图在界面解除引用后再释放。
/// </remarks>
public sealed class LivePhotoPlayer(IFrameScheduler scheduler, Func<PlaybackTools?> toolsProvider) : IDisposable
{
    private Session? _session;
    private SurfacePair? _surfaces;
    private Task _lastDecoding = Task.CompletedTask;

    public PlayerState State { get; private set; } = PlayerState.Idle;

    /// <summary>当前帧；未在播放时为 null。</summary>
    public WriteableBitmap? Surface => _surfaces?.Front;

    public event EventHandler? SurfaceInvalidated;

    public event EventHandler? StateChanged;

    /// <summary>当前解码进程号，仅供测试确认进程生命周期。</summary>
    internal int? DecoderProcessId => _session?.DecoderProcessId;

    internal FrameStore? Store => _session?.Store;

    /// <summary>
    /// 播放视频并循环，直到 <see cref="Stop"/>、再次播放或 <paramref name="cancellationToken"/> 取消。
    /// </summary>
    /// <param name="source">视频来源</param>
    /// <param name="target">显示区域尺寸（逻辑像素）</param>
    /// <param name="scaling">屏幕缩放（RenderScaling），解码尺寸 = 显示尺寸 × 缩放，不超过源尺寸</param>
    /// <param name="budget">帧内存预算</param>
    /// <param name="cancellationToken">取消即停止播放</param>
    /// <returns>首帧显示、失败或被停止时完成；失败原因见 <see cref="State"/>，取消不抛异常。</returns>
    public async Task PlayAsync(VideoSource source, PixelSize target, double scaling, PlaybackBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.Height);
        if (!double.IsFinite(scaling) || scaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaling));
        }

        Dispatcher.UIThread.VerifyAccess();
        Stop();

        // 后台线程只经由此处捕获的调度器回到界面线程，不在后台访问 Dispatcher.UIThread
        var session = new Session(Dispatcher.UIThread);
        session.Callback = now => OnFrame(session, now);
        _session = session;
        SetState(PlayerState.Loading);
        // 登记随会话存续到停止，播放开始后取消同样有效
        session.Registration = cancellationToken.Register(() => session.Dispatcher.Post(() => StopIfCurrent(session)));
        try
        {
            // 上一次播放的 FFmpeg 完全退出后再启动新的，避免两个解码进程同时运行
            await _lastDecoding;
            if (!IsCurrent(session))
            {
                return;
            }

            if (toolsProvider() is not { } tools)
            {
                Fail(session, PlaybackError.FfmpegNotFound);
                return;
            }

            if (!File.Exists(source.Path))
            {
                Fail(session, PlaybackError.SourceNotFound);
                return;
            }

            var info = await StreamInfoCache.GetAsync(tools.FfmpegPath, source, session.Token);
            if (!IsCurrent(session))
            {
                return;
            }

            if (info is not { Width: > 0, Height: > 0 })
            {
                Fail(session, PlaybackError.NoVideoStream);
                return;
            }

            if (info.IsHdr)
            {
                var filters = tools.Filters ?? await FfmpegFilters.GetAsync(tools.FfmpegPath, session.Token);
                if (!IsCurrent(session))
                {
                    return;
                }

                if (!DecodeFilterChain.IsSupported(info, filters))
                {
                    Fail(session, PlaybackError.HdrToneMapUnavailable, $"缺少 {FfmpegFilters.ZScale} 或 {FfmpegFilters.ToneMap} 滤镜");
                    return;
                }
            }

            session.Size = PlaybackGeometry.ComputeOutputSize(info.DisplayWidth, info.DisplayHeight, target, scaling);
            session.Store = FrameStore.Create(session.Size, budget);
            var input = source.ToFfmpegInput();
            _lastDecoding = Task.Run(() => DecodeLoopAsync(session, tools.FfmpegPath, input, info));
            scheduler.RequestFrame(session.Callback);
            await session.Started.Task;
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            // 被停止或取消
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Fail(session, PlaybackError.DecodeFailed, ex.Message);
        }
    }

    /// <summary>停止播放：立即结束 FFmpeg、释放帧缓存，界面位图置空。</summary>
    public void Stop()
    {
        Dispatcher.UIThread.VerifyAccess();
        EndSession(PlayerState.Idle);
    }

    /// <summary>停止并等待 FFmpeg 进程退出（退出程序前调用）。</summary>
    public async Task StopAsync()
    {
        Stop();
        await _lastDecoding;
    }

    public void Dispose() => Stop();

    private bool IsCurrent(Session session) => ReferenceEquals(_session, session);

    private void StopIfCurrent(Session session)
    {
        if (IsCurrent(session))
        {
            Stop();
        }
    }

    private void Fail(Session session, PlaybackError error, string? detail = null)
    {
        if (IsCurrent(session))
        {
            EndSession(PlayerState.Failed(error, detail));
        }
    }

    private void EndSession(PlayerState state)
    {
        var session = _session;
        _session = null;
        session?.Cancel();
        var surfaces = _surfaces;
        _surfaces = null;
        if (surfaces is not null)
        {
            SurfaceInvalidated?.Invoke(this, EventArgs.Empty);
            // 等界面解除绑定并完成本轮渲染后再释放
            Dispatcher.UIThread.Post(surfaces.Dispose, DispatcherPriority.Background);
        }

        SetState(state);
    }

    private void SetState(PlayerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrame(Session session, TimeSpan now)
    {
        if (!IsCurrent(session) || session.Store is not { } store)
        {
            return;
        }

        if (session.Origin is null && store.Count > 0)
        {
            session.Origin = now;
        }

        if (session.Origin is { } origin && store.Resolve(now - origin) is { } lookup)
        {
            var requested = now - origin;
            if (lookup.Position < requested)
            {
                // 解码跟不上：时钟停在已解码的末尾，帧到达后从原处继续而不跳帧
                session.Origin = now - lookup.Position;
            }

            if (lookup.Frame.Sequence != session.ShownSequence)
            {
                _surfaces ??= new SurfacePair(session.Size);
                _surfaces.Present(lookup.Frame.Pixels);
                session.ShownSequence = lookup.Frame.Sequence;
                SurfaceInvalidated?.Invoke(this, EventArgs.Empty);
            }

            if (IsCurrent(session) && State.Status == PlayerStatus.Loading)
            {
                SetState(PlayerState.Playing);
                session.Started.TrySetResult();
            }
        }

        if (IsCurrent(session))
        {
            scheduler.RequestFrame(session.Callback!);
        }
    }

    private async Task DecodeLoopAsync(Session session, string ffmpegPath, string input, VideoStreamInfo info)
    {
        var store = session.Store!;
        var token = session.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await using var decoder = RawVideoDecoder.Start(ffmpegPath, input, info, session.Size);
                if (!session.Attach(decoder))
                {
                    return;
                }

                var frames = 0;
                while (await store.RentAsync(token) is { } buffer)
                {
                    FrameTiming? timing;
                    try
                    {
                        timing = await decoder.ReadFrameAsync(buffer.AsMemory(0, store.FrameBytes), token);
                    }
                    catch
                    {
                        store.CancelRent(buffer);
                        throw;
                    }

                    if (timing is null)
                    {
                        store.CancelRent(buffer);
                        break;
                    }

                    if (!store.Commit(buffer, timing.Value))
                    {
                        return;
                    }

                    frames++;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                var exitCode = await decoder.WaitForExitAsync(token);
                session.Detach(decoder);
                if (frames == 0)
                {
                    Report(session, PlaybackError.DecodeFailed, $"FFmpeg 退出码 {exitCode}：{decoder.ErrorSummary}");
                    return;
                }

                // 流式模式下本轮一结束就启动下一轮：此时缓冲里还有未播放的帧，足以掩盖 FFmpeg 的启动时间
                if (!store.CompletePass() || store.IsFullyCached)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止播放
        }
        catch (Exception ex)
        {
            // 后台循环的任何异常都只能转成播放状态：抛出会在下一次播放等待本任务时冒出来
            Report(session, PlaybackError.DecodeFailed, ex.Message);
        }
    }

    private void Report(Session session, PlaybackError error, string detail) =>
        session.Dispatcher.Post(() => Fail(session, error, detail));

    private sealed class Session
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lock _lock = new();
        private RawVideoDecoder? _decoder;
        private bool _canceled;

        public Session(Dispatcher dispatcher)
        {
            Dispatcher = dispatcher;
            Token = _cancellation.Token;
        }

        public Dispatcher Dispatcher { get; }

        public CancellationToken Token { get; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<TimeSpan>? Callback { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public FrameStore? Store { get; set; }

        public PixelSize Size { get; set; }

        /// <summary>播放位置 0 对应的刷新时间戳。</summary>
        public TimeSpan? Origin { get; set; }

        public long ShownSequence { get; set; } = -1;

        public int? DecoderProcessId
        {
            get
            {
                lock (_lock)
                {
                    return _decoder?.ProcessId;
                }
            }
        }

        /// <summary>登记解码器；会话已取消时立即结束它并返回 false。</summary>
        public bool Attach(RawVideoDecoder decoder)
        {
            lock (_lock)
            {
                if (_canceled)
                {
                    decoder.Kill();
                    return false;
                }

                _decoder = decoder;
                return true;
            }
        }

        public void Detach(RawVideoDecoder decoder)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_decoder, decoder))
                {
                    _decoder = null;
                }
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                if (_canceled)
                {
                    return;
                }

                _canceled = true;
                _decoder?.Kill();
            }

            Registration.Unregister();
            _cancellation.Cancel();
            Store?.Dispose();
            Started.TrySetResult();
        }
    }
}
