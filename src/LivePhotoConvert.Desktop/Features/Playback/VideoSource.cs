using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Desktop.Features.Playback;

/// <summary>
/// 播放用的视频来源：iOS 为整段 MOV（<paramref name="IsEmbedded"/> 为 false，偏移与长度不参与读取），
/// 安卓为照片内 [<paramref name="Offset"/>, <paramref name="Offset"/> + <paramref name="Length"/>) 的内嵌视频。
/// </summary>
/// <remarks>合并阶段 2 后删除本类型，改用 Core 的 <c>LibraryItem.VideoSource</c>（字段与语义一致）。</remarks>
public sealed record VideoSource(string Path, long Offset, long Length, bool IsEmbedded);

public static class VideoSourceExtensions
{
    /// <summary>内嵌视频走 subfile 协议直接读取原文件中的片段，不切临时文件。</summary>
    public static string ToFfmpegInput(this VideoSource source) => source.IsEmbedded
        ? VideoStreamProbe.SubfileInput(source.Path, source.Offset, source.Length)
        : VideoStreamProbe.FileInput(source.Path);
}
