using LivePhotoConvert.Cli.CommandLine;
using LivePhotoConvert.Core.Models;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 命令行参数解析的测试
/// </summary>
public class CliParserTests
{
    /// <summary>
    /// 测试不带任何参数运行 CLI 会进入交互式命令模式。
    /// </summary>
    [Fact]
    public void No_Arguments_Should_Result_In_Interactive_Command()
    {
        var result = CliParser.Parse([]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.Interactive, result.Options!.Command);
    }

    /// <summary>
    /// 测试解析器能正确识别 merge 命令及其输入和输出目录。
    /// </summary>
    [Fact]
    public void Should_Parse_Merge_Command_With_Input_And_Output()
    {
        var result = CliParser.Parse(["merge", "-i", @"D:\照片", "-o", @"D:\动态照片"]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.Merge, result.Options!.Command);
        Assert.Equal(@"D:\照片", result.Options.Input);
        Assert.Equal(@"D:\动态照片", result.Options.Output);
    }

    /// <summary>
    /// 测试解析器能正确识别不同格式的源文件操作参数（例如 "move", "MOVE", "1"）。
    /// </summary>
    [Theory]
    [InlineData("keep", SourceFileAction.Keep)]
    [InlineData("move", SourceFileAction.Move)]
    [InlineData("recycle", SourceFileAction.Recycle)]
    [InlineData("delete", SourceFileAction.Delete)]
    [InlineData("0", SourceFileAction.Keep)]
    [InlineData("3", SourceFileAction.Delete)]
    [InlineData("MOVE", SourceFileAction.Move)]
    public void Should_Recognize_Various_SourceFileAction_Formats(string text, SourceFileAction expected)
    {
        var result = CliParser.Parse(["merge", "--source-action", text]);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.SourceAction);
    }

    /// <summary>
    /// 测试无法识别的源文件操作参数会导致解析错误。
    /// </summary>
    [Fact]
    public void Unrecognized_Source_Action_Should_Return_Error()
    {
        var result = CliParser.Parse(["merge", "--source-action", "burn"]);

        Assert.NotNull(result.Error);
        Assert.Null(result.Options);
    }

    /// <summary>
    /// 测试布尔类型的开关（如 --overwrite, --strict, -y）可以被正确解析。
    /// </summary>
    [Fact]
    public void Should_Parse_All_Switches()
    {
        var result = CliParser.Parse(["merge", "--overwrite", "--strict", "-y", "-p", "8"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.Overwrite);
        Assert.True(result.Options.Strict);
        Assert.True(result.Options.AssumeYes);
        Assert.Equal(8, result.Options.Parallelism);
    }

    /// <summary>
    /// 测试未知的命令会导致解析错误。
    /// </summary>
    [Fact]
    public void Unknown_Command_Should_Return_Error()
    {
        var result = CliParser.Parse(["convert"]);

        Assert.NotNull(result.Error);
        Assert.Contains("convert", result.Error);
    }

    /// <summary>
    /// 测试命令中包含未知的选项会导致解析错误。
    /// </summary>
    [Fact]
    public void Unknown_Option_Should_Return_Error()
    {
        var result = CliParser.Parse(["merge", "--turbo"]);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 测试需要一个值的选项在缺少值时会返回错误。
    /// </summary>
    [Fact]
    public void Option_Missing_Value_Should_Return_Error()
    {
        var result = CliParser.Parse(["merge", "-i"]);

        Assert.NotNull(result.Error);
        Assert.Contains("-i", result.Error);
    }

    /// <summary>
    /// 测试无效的并行数量值（例如 0, -3, "abc"）会导致解析错误。
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void Invalid_Parallelism_Value_Should_Return_Error(string value)
    {
        var result = CliParser.Parse(["merge", "-p", value]);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 测试拆分命令不接受原始文件处理方式选项。
    /// </summary>
    [Fact]
    public void Split_Command_Should_Not_Accept_Source_Action()
    {
        // 拆分不会碰原始文件，给了这个参数说明用户理解有误，应该明确拒绝而不是默默忽略
        var result = CliParser.Parse(["split", "-s", "delete"]);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 测试拆分命令不接受严格配对校验选项。
    /// </summary>
    [Fact]
    public void Split_Command_Should_Not_Accept_Strict_Matching()
    {
        var result = CliParser.Parse(["split", "--strict"]);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 测试拆分命令支持 --format / -f 参数。
    /// </summary>
    [Theory]
    [InlineData("android", SplitTargetFormat.Android)]
    [InlineData("apple", SplitTargetFormat.Apple)]
    [InlineData("ios", SplitTargetFormat.Apple)]
    [InlineData("default", SplitTargetFormat.Android)]
    public void Split_Command_Should_Accept_Format(string formatText, SplitTargetFormat expected)
    {
        var result = CliParser.Parse(["split", "--format", formatText]);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options!.SplitFormat);
        Assert.True(result.Options.ExplicitSplitFormat);
    }

    /// <summary>
    /// 测试非 split 命令使用 --format 会报错。
    /// </summary>
    [Fact]
    public void Merge_Command_Should_Not_Accept_Format()
    {
        var result = CliParser.Parse(["merge", "--format", "apple"]);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// 测试解析器能识别帮助参数。
    /// </summary>
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    [InlineData("/?")]
    public void Should_Recognize_Help_Arguments(string argument)
    {
        var result = CliParser.Parse([argument]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.Help, result.Options!.Command);
    }

    /// <summary>
    /// 测试解析器能识别版本参数。
    /// </summary>
    [Theory]
    [InlineData("-v")]
    [InlineData("--version")]
    public void Should_Recognize_Version_Arguments(string argument)
    {
        var result = CliParser.Parse([argument]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.Version, result.Options!.Command);
    }

    /// <summary>
    /// 测试解析器能正确解析外部工具（如 ExifTool 和 FFmpeg）的路径。
    /// </summary>
    [Fact]
    public void Should_Parse_External_Tool_Paths()
    {
        var result = CliParser.Parse(["merge", "--exiftool", @"C:\tools\exiftool.exe", "--ffmpeg", @"C:\tools\ffmpeg.exe"]);

        Assert.Null(result.Error);
        Assert.Equal(@"C:\tools\exiftool.exe", result.Options!.ExifToolPath);
        Assert.Equal(@"C:\tools\ffmpeg.exe", result.Options.FfmpegPath);
    }

    /// <summary>
    /// 测试解析器能识别 tools / download-tools 命令。
    /// </summary>
    [Theory]
    [InlineData("tools")]
    [InlineData("download-tools")]
    public void Should_Parse_DownloadTools_Command(string command)
    {
        var result = CliParser.Parse([command]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.DownloadTools, result.Options!.Command);
    }

    /// <summary>
    /// 测试解析器能正确解析 --auto-download 和 --mirror 选项。
    /// </summary>
    [Fact]
    public void Should_Parse_AutoDownload_And_Mirror_Options()
    {
        var result = CliParser.Parse(["merge", "--auto-download", "--mirror", "https://ghfast.top/"]);

        Assert.Null(result.Error);
        Assert.True(result.Options!.AutoDownload);
        Assert.Equal("https://ghfast.top/", result.Options.CustomMirror);
    }

    /// <summary>
    /// 测试 --mirror 缺少参数值时报错。
    /// </summary>
    [Fact]
    public void Missing_Mirror_Value_Should_Return_Error()
    {
        var result = CliParser.Parse(["merge", "--mirror"]);

        Assert.NotNull(result.Error);
    }
}
