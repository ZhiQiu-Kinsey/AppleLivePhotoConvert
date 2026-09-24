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

    public static HeifDecoder RequireHeifDecoder() =>
        HeifDecoder.TryCreate() ?? SkipDecoder();

    /// <summary>
    /// libultrahdr 的 ultrahdr_app，用作 Ultra HDR 判定与 HDR 解码的参考实现；
    /// 通常不在 PATH 中，可用环境变量 LPC_ULTRAHDR_APP 指定。
    /// </summary>
    public static string RequireUltraHdrApp()
    {
        var path = Environment.GetEnvironmentVariable("LPC_ULTRAHDR_APP");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        return ToolLocator.Find(OperatingSystem.IsWindows() ? "ultrahdr_app.exe" : "ultrahdr_app") ?? Skip("ultrahdr_app（可设置 LPC_ULTRAHDR_APP）");
    }

    private static HeifDecoder SkipDecoder()
    {
        Skip("heif-dec / heif-convert");
        return null!;
    }

    private static string Skip(string tool)
    {
        Assert.Skip($"未安装 {tool}，跳过集成测试。");
        return string.Empty;
    }
}
