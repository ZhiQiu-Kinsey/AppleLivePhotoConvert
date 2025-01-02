using System.Diagnostics;

namespace LivePhotoConvert;

/// <summary>
/// 工具类
/// </summary>
public static class UtilityHelp
{
    private const string ExifToolPath = @".\ExifTool\ExifTool.exe";
    private static readonly object ConsoleLock = new();


    /// <summary>
    /// 创建ExifTool配置文件
    /// </summary>
    /// <returns>配置文件路径</returns>
    public static string CreateExifToolConfig()
    {
        const string configFile = "LivePhotoExif.config";
        if (File.Exists(configFile))
        {
            return configFile;
        }

        const string configContent = """
                                     %Image::ExifTool::UserDefined = (
                                        'Image::ExifTool::XMP::Main' => {
                                            GCamera => {
                                                SubDirectory => {
                                                    TagTable => 'Image::ExifTool::UserDefined::GCamera',
                                                },
                                            }
                                        },
                                     );
                                     %Image::ExifTool::UserDefined = (
                                        'Image::ExifTool::Exif::Main' => {
                                            0x8897 => { Name => 'MicroVideo', Writable => 'int8u' },
                                        },
                                     );
                                     %Image::ExifTool::UserDefined::GCamera = (
                                        GROUPS => { 0 => 'XMP', 1 => 'XMP-GCamera', 2 => 'Image' },
                                        NAMESPACE   => { 'GCamera' => 'http://ns.google.com/photos/1.0/camera/' },
                                        WRITABLE    => 'string',
                                        MicroVideo  => { Writable => 'integer' },
                                        MicroVideoVersion => { Writable => 'integer' },
                                        MicroVideoOffset => { Writable => 'integer' },
                                        MicroVideoPresentationTimestampUs => { Writable => 'integer' },
                                     );
                                     """;
        File.WriteAllText(configFile, configContent);
        return configFile;
    }


    /// <summary>
    /// 添加Exif元数据
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="photoFilesize">原图片字节长度</param>
    /// <param name="mergedFilesize">合成后的图片字节长度</param>
    public static void InsertExifMetadata(string imagePath, long photoFilesize, long mergedFilesize)
    {
        // 计算偏移量
        long offset = mergedFilesize - photoFilesize;
        string configPath = CreateExifToolConfig();
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = ExifToolPath,
                Arguments = $"-config \"{configPath}\" " +
                            $"-XMP-GCamera:MicroVideo=1 " +
                            $"-XMP-GCamera:MicroVideoVersion=1 " +
                            $"-XMP-GCamera:MicroVideoOffset={offset} " +
                            $"-XMP-GCamera:MicroVideoPresentationTimestampUs={offset / 2} " +
                            $"-MicroVideo=1 " +
                            $"-overwrite_original \"{imagePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd();
            throw new Exception($"ExifTool添加元数据失败:{error}");
        }
    }

    /// <summary>
    /// 删除XMP数据以及特定的EXIF标签
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    public static void RemoveXmpAndExifTags(string imagePath)
    {
        string configPath = CreateExifToolConfig();
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = ExifToolPath,
                // 简写
                // Arguments = $"-XMP:ALL= -EXIF:0x8897= -overwrite_original \"{imagePath}\"",
                Arguments = $"-config \"{configPath}\" " +
                            $"-XMP-GCamera:MicroVideo= " +
                            $"-XMP-GCamera:MicroVideoVersion= " +
                            $"-XMP-GCamera:MicroVideoOffset= " +
                            $"-XMP-GCamera:MicroVideoPresentationTimestampUs= " +
                            $"-MicroVideo= " +
                            $"-overwrite_original \"{imagePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd();
            throw new Exception($"ExifTool删除元数据失败: {error}");
        }
    }

    /// <summary>
    /// 使用ExifTool获取MicroVideoOffset标签
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <returns>偏移量</returns>
    /// <exception cref="Exception">不是动态照片类型</exception>
    public static long GetMicroVideoOffset(string imagePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExifToolPath,
            Arguments = $"-MicroVideoOffset \"{imagePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)!;
        using var reader = process.StandardOutput;
        string output = reader.ReadToEnd();
        if (string.IsNullOrEmpty(output))
        {
            throw new Exception("该图片不是动态照片!");
        }

        // 提取偏移量值
        var offset = output.Split(':')[1].Trim();
        if (string.IsNullOrEmpty(offset))
        {
            throw new Exception("该图片不是动态照片!");
        }

        return long.Parse(offset);
    }

    /// <summary>
    /// 选择文件夹路径，并验证路径是否有效
    /// </summary>
    /// <param name="message">提示信息</param>
    /// <returns>有效的文件夹路径，如果用户取消则返回 null</returns>
    public static string SelectFolder(string message)
    {
        while (true)
        {
            // 提示用户输入
            Console.WriteLine($"{message}（输入文件夹路径或拖动文件夹到控制台（或输入 'q' 退出））：");
            string? input = Console.ReadLine()?.Replace("\"", "").Trim();
            // 检查是否退出
            if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("操作已取消。");
                Environment.Exit(0);
            }

            // 检查路径是否有效
            if (Directory.Exists(input))
            {
                return input;
            }

            // 提示路径无效
            Console.WriteLine("无效的目录路径，请重新输入。");
        }
    }

    /// <summary>
    /// 打印居中标题
    /// </summary>
    /// <param name="text">打印文本</param>
    /// <param name="color">颜色</param>
    public static void Print(string text, ConsoleColor color = ConsoleColor.Red)
    {
        Console.ForegroundColor = color;
        int consoleWidth = Console.WindowWidth;
        // 文本两侧各加两个空格
        int dashLength = Math.Max(0, (consoleWidth - (text.Length * 2) - 2) / 2);
        string dashes = new('-', dashLength);
        string header = $"{dashes} {text} {dashes}";
        // 如果总长度不足一行，补充分割线
        if (header.Length < consoleWidth)
        {
            header += new string('-', consoleWidth - header.Length - text.Length);
        }
        Console.WriteLine(header);
        Console.ResetColor();
    }

    /// <summary>
    /// 打印进度条
    /// </summary>
    /// <param name="completed">已完成数量</param>
    /// <param name="total">总数量</param>
    /// <param name="fileName">文件名</param>
    /// <param name="barLength">进度条长度</param>
    public static void DrawProgressBar(int completed, int total, string fileName, int barLength = 80)
    {
        if (total == 0) return;

        lock (ConsoleLock)
        {
            // 计算进度并保留两位小数
            double progress = total == 0 ? 0 : (double)completed / total;
            int filled = (int)(progress * barLength);

            // 处理文件名，超过 15 个字符时显示 "..."
            string displayName = fileName.Length > 20 ? $"...{fileName[^17..]}" : fileName;

            // 构建进度条字符串，显示百分比并保留两位小数
            string progressBar = $"正在处理: {displayName} [{new string('=', filled)}{new string(' ', barLength - filled)}] {progress:P2}";

            // 移动光标到行首并输出进度条
            Console.SetCursorPosition(0, Console.CursorTop);
            // 用空格覆盖当前行的内容
            Console.Write(new string(' ', Console.WindowWidth));
            // 将光标重新移动到行首
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(progressBar);
            Console.Out.Flush();
        }
    }
}