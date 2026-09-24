using LivePhotoConvert.Core.Media;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Services;

internal static class ImageInspector
{
    /// <summary>
    /// JPEG 由托管代码直接解析；其它格式（HEIC 动态照片）的 XMP 需要借助 ExifTool 读取。
    /// </summary>
    public static async Task<ImageLayout> InspectAsync(string path, IMetadataService metadata, CancellationToken cancellationToken)
    {
        var layout = MotionPhotoLayout.Inspect(path);
        if (layout.Video is not null || MediaFileTypes.HasJpegExtension(path))
        {
            return layout;
        }

        var xmp = await metadata.ReadXmpAsync(path, cancellationToken);
        return xmp is null ? layout : MotionPhotoLayout.Inspect(path, xmp);
    }

    /// <summary>
    /// 读取指定偏移处的文件头并嗅探格式，防止扩展名与实际内容不符。
    /// </summary>
    public static string SniffExtension(string path, long offset, Func<ReadOnlySpan<byte>, string> sniff)
    {
        Span<byte> header = stackalloc byte[64];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        return sniff(header[..stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)]);
    }
}
