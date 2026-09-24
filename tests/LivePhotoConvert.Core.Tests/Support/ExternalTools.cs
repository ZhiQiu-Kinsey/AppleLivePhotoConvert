using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Metadata;

namespace LivePhotoConvert.Core.Tests.Support;

/// <summary>
/// 真实外部工具的集成测试：机器上没有对应工具时跳过。
/// </summary>
internal static class ExternalTools
{
    public static string RequireExifTool() =>
        ToolLocator.Find(ExifToolMetadataService.ExecutableName) ?? Skip("ExifTool");

    public static string RequireFfmpeg() =>
        ToolLocator.Find(FfmpegVideoConverter.ExecutableName) ?? Skip("FFmpeg");

    public static string RequireHeifEnc() =>
        ToolLocator.Find(HeifEncImageConverter.ExecutableName) ?? Skip("heif-enc");

    private static string Skip(string tool)
    {
        Assert.Skip($"未安装 {tool}，跳过集成测试。");
        return string.Empty;
    }
}
