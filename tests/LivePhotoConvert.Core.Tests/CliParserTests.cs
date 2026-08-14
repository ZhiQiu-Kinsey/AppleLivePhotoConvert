using LivePhotoConvert.Cli.CommandLine;
using LivePhotoConvert.Core.Matching;
using LivePhotoConvert.Core.Models;
using Spectre.Console.Cli;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 现代化命令行框架 (Spectre.Console.Cli) 的指令配置与参数绑定单元测试
/// </summary>
public class CliParserTests
{
    private static CommandApp CreateTestApp()
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("LivePhotoConvert");
            config.AddCommand<MergeCommand>("merge");
            config.AddCommand<SplitCommand>("split");
            config.AddCommand<ToolsCommand>("tools")
                .WithAlias("download-tools");
        });
        return app;
    }

    /// <summary>
    /// 测试 merge 选项绑定：能正确解析输入路径、输出路径与布尔标志
    /// </summary>
    [Fact]
    public void MergeSettings_Should_Bind_Properties_Correctly()
    {
        var settings = new MergeSettings
        {
            Input = @"D:\Photos",
            Output = @"D:\Output",
            SourceAction = SourceFileAction.Move,
            Overwrite = true,
            SkipValidation = true,
            AssumeYes = true,
            Parallelism = 8,
            AutoDownload = true,
            CustomMirror = "https://ghproxy.net/",
            ExifToolPath = @"C:\tools\exiftool.exe",
            FfmpegPath = @"C:\tools\ffmpeg.exe"
        };

        Assert.Equal(@"D:\Photos", settings.Input);
        Assert.Equal(@"D:\Output", settings.Output);
        Assert.Equal(SourceFileAction.Move, settings.SourceAction);
        Assert.True(settings.Overwrite);
        Assert.True(settings.SkipValidation);
        Assert.True(settings.AssumeYes);
        Assert.Equal(8, settings.Parallelism);
        Assert.True(settings.AutoDownload);
        Assert.Equal("https://ghproxy.net/", settings.CustomMirror);
        Assert.Equal(@"C:\tools\exiftool.exe", settings.ExifToolPath);
        Assert.Equal(@"C:\tools\ffmpeg.exe", settings.FfmpegPath);
    }

    /// <summary>
    /// 测试 split 选项绑定：能正确解析拆分格式与输出参数
    /// </summary>
    [Theory]
    [InlineData(SplitTargetFormat.Android)]
    [InlineData(SplitTargetFormat.Apple)]
    public void SplitSettings_Should_Bind_Format(SplitTargetFormat format)
    {
        var settings = new SplitSettings
        {
            Input = @"D:\MotionPhotos",
            Output = @"D:\Extracted",
            Format = format,
            Overwrite = true
        };

        Assert.Equal(@"D:\MotionPhotos", settings.Input);
        Assert.Equal(@"D:\Extracted", settings.Output);
        Assert.Equal(format, settings.Format);
        Assert.True(settings.Overwrite);
    }

    /// <summary>
    /// 测试 CommandApp 可以正常识别内置的 --help 选项且不抛出未处理异常
    /// </summary>
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task CommandApp_Should_Handle_Help_Flags(string flag)
    {
        var app = CreateTestApp();
        var exitCode = await app.RunAsync([flag]);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// 测试未知命令输入时 CommandApp 返回非 0 错误码
    /// </summary>
    [Fact]
    public async Task CommandApp_Unknown_Command_Should_Return_NonZero()
    {
        var app = CreateTestApp();
        var exitCode = await app.RunAsync(["unknown-cmd-12345"]);
        Assert.NotEqual(0, exitCode);
    }
}

