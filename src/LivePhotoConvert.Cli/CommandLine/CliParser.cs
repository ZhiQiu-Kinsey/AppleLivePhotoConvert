using System.Globalization;

namespace LivePhotoConvert.Cli.CommandLine;

/// <summary>
/// 命令行参数解析
/// </summary>
/// <remarks>命令简单且需要保持 AOT 友好，因此手写解析而不引入额外依赖。</remarks>
static class CliParser
{
    /// <summary>
    /// 解析命令行参数
    /// </summary>
    /// <param name="args">参数数组</param>
    /// <returns>解析结果</returns>
    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return ParseResult.Success(new CliOptions { Command = CliCommand.Interactive });
        }

        var first = args[0];
        switch (first.ToLowerInvariant())
        {
            case "-h" or "--help" or "help" or "/?":
                return ParseResult.Success(new CliOptions { Command = CliCommand.Help });
            case "-v" or "--version" or "version":
                return ParseResult.Success(new CliOptions { Command = CliCommand.Version });
        }

        var command = first.ToLowerInvariant() switch
        {
            "merge" => CliCommand.Merge,
            "split" => CliCommand.Split,
            "tools" or "download-tools" => CliCommand.DownloadTools,
            _ => (CliCommand?)null
        };

        if (command is null)
        {
            return ParseResult.Failure($"未知的命令 \"{first}\"。可用命令：merge、split、tools。");
        }

        var options = new CliOptions { Command = command.Value };
        for (var index = 1; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--auto-download" or "--download-tools":
                    options = options with { AutoDownload = true };
                    break;

                case "--mirror" or "--custom-mirror":
                    if (!TryTakeValue(args, ref index, argument, out var customMirror, out var mirrorError))
                    {
                        return ParseResult.Failure(mirrorError);
                    }
                    options = options with { CustomMirror = customMirror };
                    break;

                case "-i" or "--input":
                    if (!TryTakeValue(args, ref index, argument, out var input, out var inputError))
                    {
                        return ParseResult.Failure(inputError);
                    }

                    options = options with { Input = input };
                    break;

                case "-o" or "--output":
                    if (!TryTakeValue(args, ref index, argument, out var output, out var outputError))
                    {
                        return ParseResult.Failure(outputError);
                    }

                    options = options with { Output = output };
                    break;

                case "-a" or "--action" or "-s" or "--source-action":
                    if (command == CliCommand.Split)
                    {
                        return ParseResult.Failure("拆分命令不支持指定原始文件处理方式。");
                    }

                    if (!TryTakeValue(args, ref index, argument, out var action, out var actionError))
                    {
                        return ParseResult.Failure(actionError);
                    }

                    if (!TryParseSourceAction(action, out var parsedAction))
                    {
                        return ParseResult.Failure(
                            $"未知的原始文件处理方式 \"{action}\"。可用值：Keep (0)、Move (1)、Recycle (2)、Delete (3)。");
                    }

                    options = options with { SourceAction = parsedAction };
                    break;

                case "--overwrite":
                    options = options with { Overwrite = true };
                    break;

                case "--strict":
                    if (command == CliCommand.Split)
                    {
                        return ParseResult.Failure("拆分命令不支持 --strict 严格匹配选项。");
                    }

                    options = options with { Strict = true };
                    break;

                case "--exiftool":
                    if (!TryTakeValue(args, ref index, argument, out var exifTool, out var exifToolError))
                    {
                        return ParseResult.Failure(exifToolError);
                    }

                    options = options with { ExifToolPath = exifTool };
                    break;

                case "--ffmpeg":
                    if (!TryTakeValue(args, ref index, argument, out var ffmpeg, out var ffmpegError))
                    {
                        return ParseResult.Failure(ffmpegError);
                    }

                    options = options with { FfmpegPath = ffmpeg };
                    break;

                case "-y" or "--yes":
                    options = options with { AssumeYes = true };
                    break;

                case "-p" or "--parallelism":
                    if (!TryTakeValue(args, ref index, argument, out var parallelism, out var parallelismError))
                    {
                        return ParseResult.Failure(parallelismError);
                    }

                    if (!int.TryParse(parallelism, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    {
                        return ParseResult.Failure($"并发数必须是大于 0 的整数：\"{parallelism}\"");
                    }

                    options = options with { Parallelism = value };
                    break;

                case "-f" or "--format" or "--target-format":
                    if (command != CliCommand.Split)
                    {
                        return ParseResult.Failure("只有拆分命令 split 支持指定输出格式 --format。");
                    }

                    if (!TryTakeValue(args, ref index, argument, out var formatText, out var formatError))
                    {
                        return ParseResult.Failure(formatError);
                    }

                    if (!TryParseSplitFormat(formatText, out var parsedFormat))
                    {
                        return ParseResult.Failure(
                            $"未知的拆分目标格式 \"{formatText}\"。可用值：android (标准安卓格式 .jpg/.heic + .mp4)、apple (苹果实况照片 .jpg/.heic + .mov)。");
                    }

                    options = options with { SplitFormat = parsedFormat, ExplicitSplitFormat = true };
                    break;

                default:
                    return ParseResult.Failure($"未知的参数：\"{argument}\"");
            }
        }

        return ParseResult.Success(options);
    }

    private static bool TryParseSplitFormat(string text, out SplitTargetFormat format)
    {
        switch (text.ToLowerInvariant())
        {
            case "android" or "default" or "standard" or "0":
                format = SplitTargetFormat.Android;
                return true;
            case "apple" or "ios" or "livephoto" or "1":
                format = SplitTargetFormat.Apple;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static bool TryParseSourceAction(string text, out SourceFileAction action)
    {
        if (Enum.TryParse(text, ignoreCase: true, out action))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && Enum.IsDefined(typeof(SourceFileAction), num))
        {
            action = (SourceFileAction)num;
            return true;
        }

        action = default;
        return false;
    }

    /// <summary>
    /// 获取跟随在选项后面的值参数
    /// </summary>
    private static bool TryTakeValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out string value,
        out string error)
    {
        var next = index + 1;
        if (next >= args.Count)
        {
            value = string.Empty;
            error = $"选项 {optionName} 缺少值参数。";
            return false;
        }

        value = args[next];
        index = next;
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// 命令行参数解析结果
/// </summary>
sealed record ParseResult(CliOptions? Options, string? Error)
{
    public static ParseResult Success(CliOptions options) => new(options, null);
    public static ParseResult Failure(string error) => new(null, error);
}
