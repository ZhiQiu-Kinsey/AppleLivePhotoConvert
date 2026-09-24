using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;
using LivePhotoConvert.Desktop.Features.Tasks;

namespace LivePhotoConvert.Desktop.Features.Library;

/// <summary>启动前检查外部工具是否可用；测试可替换。</summary>
public interface IToolAvailability
{
    /// <summary>可能启动外部进程探测版本，调用方应在后台线程调用。</summary>
    bool IsAvailable(RequiredTool tool, ToolPaths paths);
}

/// <summary>查找规则与 <see cref="ExternalToolEngines"/> 创建引擎时一致，检查通过即意味着任务能创建出对应引擎。</summary>
public sealed class ToolAvailability : IToolAvailability
{
    public static ToolAvailability Instance { get; } = new();

    public bool IsAvailable(RequiredTool tool, ToolPaths paths) => tool switch
    {
        RequiredTool.ExifTool => ToolLocator.Find(ExifToolMetadataService.ExecutableName, paths.ExifTool, "ExifTool", "exiftool") is not null,
        RequiredTool.Ffmpeg => ToolLocator.Find(FfmpegVideoConverter.ExecutableName, paths.Ffmpeg, "ffmpeg", "FFmpeg", "bin") is not null,
        RequiredTool.HeicEncoder => MagickImageConverter.SupportsHeicEncoding
            || ToolLocator.Find(HeifEncImageConverter.ExecutableName, paths.HeifEnc, "heif-enc", "libheif", "bin") is not null,
        _ => false
    };
}
