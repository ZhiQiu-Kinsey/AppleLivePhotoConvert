using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Media;

namespace LivePhotoConvert.Desktop.Features.Playback;

public static class VideoSourceExtensions
{
    /// <summary>内嵌视频走 subfile 协议直接读取原文件中的片段，不切临时文件。</summary>
    public static string ToFfmpegInput(this VideoSource source) => source.IsEmbedded
        ? VideoStreamProbe.SubfileInput(source.Path, source.Offset, source.Length)
        : VideoStreamProbe.FileInput(source.Path);
}
