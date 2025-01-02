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
    /// <returns></returns>
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
    /// 删除 XMP 数据以及特定的 EXIF 标签
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
    /// <param name="imagePath"></param>
    /// <returns></returns>
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
    /// 选择文件夹
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public static string? SelectFolder(string message)
    {
        Console.WriteLine($"{message},输入文件夹路径或拖动文件夹到控制台:");
        string? input = Console.ReadLine()?.Replace("\"", "").TrimEnd();
        if (Directory.Exists(input))
        {
            return input;
        }
        else
        {
            Console.WriteLine("无效的目录路径。");
            return null;
        }
    }

    /// <summary>
    /// 打印进度条
    /// </summary>
    /// <param name="completed">已完成数量</param>
    /// <param name="total">总数量</param>
    /// <param name="fileName">文件名</param>
    /// <param name="barLength">进度条长度</param>
    public static void DrawProgressBar(int completed, int total, string fileName, int barLength = 70)
    {
        if (total == 0) return;
        lock (ConsoleLock)
        {
            // 计算进度并保留两位小数
            double progress = (double)completed / total;
            int filled = (int)(progress * barLength);
            // 处理文件名，超过 15 个字符时显示 "..."
            if (fileName.Length > 15)
            {
                fileName = $"...{fileName.Substring(fileName.Length - 12, 12)}";
            }

            // 构建进度条字符串，显示百分比并保留两位小数
            string progressBar = $"正在处理: {fileName} [{new string('=', filled)}{new string(' ', barLength - filled)}] {progress:P2}";
            // 移动光标到行首并输出进度条
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(progressBar);
            Console.Out.Flush();
        }
    }
}