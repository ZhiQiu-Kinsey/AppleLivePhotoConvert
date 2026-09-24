namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>播放错误到界面文案的映射；键写成字面量，字符串资源测试才能核对引用。</summary>
public static class PlaybackTexts
{
    public static string ErrorKey(PlaybackError error) => error switch
    {
        PlaybackError.FfmpegNotFound => "PlaybackErrorFfmpegMissing",
        PlaybackError.SourceNotFound => "PlaybackErrorSourceMissing",
        PlaybackError.NoVideoStream => "PlaybackErrorNoVideoStream",
        PlaybackError.HdrToneMapUnavailable => "PlaybackErrorHdrToneMap",
        _ => "PlaybackErrorDecodeFailed"
    };

    /// <summary>更换或安装 FFmpeg 能解决的错误，界面据此提供前往依赖页的入口。</summary>
    public static bool IsToolProblem(PlaybackError error) => error is PlaybackError.FfmpegNotFound or PlaybackError.HdrToneMapUnavailable;
}
