namespace LivePhotoConvert.Desktop.Features.Playback;

public enum PlayerStatus
{
    Idle,
    Loading,
    Playing,
    Error
}

public enum PlaybackError
{
    None,

    /// <summary>找不到可用的 FFmpeg。</summary>
    FfmpegNotFound,

    /// <summary>视频文件不存在。</summary>
    SourceNotFound,

    /// <summary>FFmpeg 读不到视频流或尺寸。</summary>
    NoVideoStream,

    /// <summary>HDR 视频需要 zscale 与 tonemap 滤镜，当前 FFmpeg 缺少；不静默降级为发灰的画面。</summary>
    HdrToneMapUnavailable,

    /// <summary>FFmpeg 启动失败或一帧也没有解出。</summary>
    DecodeFailed
}

/// <param name="Detail">诊断信息（如 FFmpeg 最后一行错误），不直接展示给用户</param>
public readonly record struct PlayerState(PlayerStatus Status, PlaybackError Error = PlaybackError.None, string? Detail = null)
{
    public static PlayerState Idle => new(PlayerStatus.Idle);

    public static PlayerState Loading => new(PlayerStatus.Loading);

    public static PlayerState Playing => new(PlayerStatus.Playing);

    public static PlayerState Failed(PlaybackError error, string? detail = null) => new(PlayerStatus.Error, error, detail);
}
