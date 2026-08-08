using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LivePhotoConvert;

/// <summary>
/// 合成成功后对输入目录中【已匹配】原始文件的处理方式
/// </summary>
public enum SourceFileAction
{
    /// <summary>
    /// 保留原始文件（默认）
    /// </summary>
    Keep = 0,

    /// <summary>
    /// 移动到输入目录下的子文件夹
    /// </summary>
    Move = 1,

    /// <summary>
    /// 删除到回收站（仅 Windows）
    /// </summary>
    Recycle = 2,

    /// <summary>
    /// 永久删除（不可恢复）
    /// </summary>
    Delete = 3
}

/// <summary>
/// 工具类
/// </summary>
public static class UtilityHelp
{
    private const string ExifToolPath = @".\ExifTool\ExifTool.exe";
    private static readonly object ConsoleLock = new();

    /// <summary>
    /// 移动模式下，存放已合成原始文件的子文件夹名称
    /// </summary>
    public const string MergedFolderName = "已合成";


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
        var offset = mergedFilesize - photoFilesize;
        var configPath = CreateExifToolConfig();
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
            var error = process.StandardError.ReadToEnd();
            throw new Exception($"ExifTool添加元数据失败:{error}");
        }
    }

    /// <summary>
    /// 删除XMP数据以及特定的EXIF标签
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    public static void RemoveXmpAndExifTags(string imagePath)
    {
        var configPath = CreateExifToolConfig();
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
            var error = process.StandardError.ReadToEnd();
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
        var output = reader.ReadToEnd();
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
    /// 是否动态照片
    /// </summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="videoPath">视频路径</param>
    /// <returns>偏移量</returns>
    /// <exception cref="Exception">不是动态照片类型或照片与视频不匹配</exception>
    public static long IsLivePhoto(string imagePath, string videoPath)
    {
        // 获取照片的 Content Identifier
        var imageContentId = GetContentIdentifier(imagePath, "-Apple:Content Identifier");
        // 获取视频的 Content Identifier
        var videoContentId = GetContentIdentifier(videoPath, "-Keys:Content Identifier");
        // 检查照片和视频的 Content Identifier 是否匹配
        if (imageContentId != videoContentId)
        {
            throw new Exception("照片和视频的 Content Identifier 不匹配!");
        }
        
        // 获取 MicroVideoOffset
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
        var output = reader.ReadToEnd();
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
    /// 获取文件中的 Content Identifier
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="tagName">标签名称</param>
    /// <returns>Content Identifier</returns>
    public static string GetContentIdentifier(string filePath, string tagName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExifToolPath,
            Arguments = $"\"{tagName}\" \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)!;
        using var reader = process.StandardOutput;
        var output = reader.ReadToEnd();

        // 提取 Content Identifier
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("Content Identifier"))
            {
                return line.Split(':')[1].Trim();
            }
        }

        return string.Empty;
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
            var input = Console.ReadLine()?.Replace("\"", "").Trim();
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
        var consoleWidth = Console.WindowWidth;
        // 文本两侧各加两个空格
        var dashLength = Math.Max(0, (consoleWidth - (text.Length * 2) - 2) / 2);
        string dashes = new('-', dashLength);
        var header = $"{dashes} {text} {dashes}";
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
        if (total == 0)
        {
            return;
        }

        lock (ConsoleLock)
        {
            // 计算进度并保留两位小数
            var progress = total == 0 ? 0 : (double)completed / total;
            var filled = (int)(progress * barLength);

            // 处理文件名，超过 15 个字符时显示 "..."
            var displayName = fileName.Length > 20 ? $"...{fileName[^17..]}" : fileName;

            // 构建进度条字符串，显示百分比并保留两位小数
            var progressBar = $"正在处理: {displayName} [{new string('=', filled)}{new string(' ', barLength - filled)}] {progress:P2}";

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

    /// <summary>
    /// 询问合成成功后如何处理输入目录中【已匹配】的原始文件
    /// </summary>
    /// <returns>用户选择的处理方式</returns>
    public static SourceFileAction AskSourceFileAction()
    {
        while (true)
        {
            Console.WriteLine("合成成功后，如何处理输入目录中【已匹配】的原始照片与视频？");
            Console.WriteLine("  0. 保留（默认，直接回车即为此项）");
            Console.WriteLine($"  1. 移动到输入目录下的 \"{MergedFolderName}\" 子文件夹");
            Console.WriteLine("  2. 删除到回收站（仅 Windows 可用）");
            Console.WriteLine("  3. 永久删除（不可恢复，需二次确认）");
            Console.WriteLine("未匹配的文件在任何选项下都不会被处理。请输入选项：");

            var input = Console.ReadLine()?.Trim();
            // 直接回车即保留
            if (string.IsNullOrEmpty(input) || input == "0")
            {
                Console.WriteLine("已选择：保留原始文件。");
                return SourceFileAction.Keep;
            }

            switch (input)
            {
                case "1":
                    Console.WriteLine($"已选择：合成成功后将原始文件移动到 \"{MergedFolderName}\" 子文件夹。");
                    return SourceFileAction.Move;
                case "2":
                    // 回收站依赖 Windows Shell，其他平台没有等价实现，降级为保留
                    if (!OperatingSystem.IsWindows())
                    {
                        Console.WriteLine("当前系统不是 Windows，无法使用回收站，已自动降级为：保留原始文件。");
                        return SourceFileAction.Keep;
                    }

                    Console.WriteLine("已选择：合成成功后将原始文件删除到回收站。");
                    return SourceFileAction.Recycle;
                case "3":
                    Console.WriteLine("警告：永久删除后文件无法恢复！确认请输入 Y，输入其他内容将改为保留：");
                    var confirm = Console.ReadLine()?.Trim();
                    if (!string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("未确认，已改为：保留原始文件。");
                        return SourceFileAction.Keep;
                    }

                    Console.WriteLine("已选择：合成成功后永久删除原始文件。");
                    return SourceFileAction.Delete;
                default:
                    Console.WriteLine("无效的选项，请重新输入。");
                    break;
            }
        }
    }

    /// <summary>
    /// 按指定方式清理一组已合成成功的原始文件
    /// </summary>
    /// <remarks>单个文件处理失败只会被记录到 <paramref name="failures"/>，不会中断流程，已合成好的照片不受影响。</remarks>
    /// <param name="filePaths">需要清理的原始文件路径，调用方必须保证这些文件已匹配成功且合成校验通过</param>
    /// <param name="action">处理方式</param>
    /// <param name="inputPath">输入目录，移动模式下子文件夹建在此目录下</param>
    /// <param name="failures">失败信息收集列表</param>
    /// <returns>成功处理的文件数量</returns>
    public static int CleanupSourceFiles(IEnumerable<string> filePaths, SourceFileAction action, string inputPath, ICollection<string> failures)
    {
        if (action == SourceFileAction.Keep)
        {
            return 0;
        }

        string? mergedDirectory = null;
        if (action == SourceFileAction.Move)
        {
            mergedDirectory = Path.Combine(inputPath, MergedFolderName);
            try
            {
                Directory.CreateDirectory(mergedDirectory);
            }
            catch (Exception ex)
            {
                // 子文件夹建不出来时逐个记为失败，不向外抛，避免把清理问题误报成合成失败
                foreach (var filePath in filePaths)
                {
                    failures.Add($"{filePath}：无法创建 \"{MergedFolderName}\" 子文件夹，{ex.Message}");
                }

                return 0;
            }
        }

        var cleaned = 0;
        foreach (var filePath in filePaths)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                switch (action)
                {
                    case SourceFileAction.Move:
                        // 重名时追加 _1、_2 后缀，绝不覆盖已有文件
                        File.Move(filePath, GetUniqueDestinationPath(mergedDirectory!, Path.GetFileName(filePath)));
                        break;
                    case SourceFileAction.Recycle:
                        if (!OperatingSystem.IsWindows())
                        {
                            failures.Add($"{filePath}：当前系统不支持回收站。");
                            continue;
                        }

                        SendToRecycleBin(filePath);
                        break;
                    case SourceFileAction.Delete:
                        File.Delete(filePath);
                        break;
                    case SourceFileAction.Keep:
                    default:
                        continue;
                }

                cleaned++;
            }
            catch (Exception ex)
            {
                failures.Add($"{filePath}：{ex.Message}");
            }
        }

        return cleaned;
    }

    /// <summary>
    /// 在目标目录中获取不冲突的文件路径，重名时依次追加 _1、_2
    /// </summary>
    /// <param name="directory">目标目录</param>
    /// <param name="fileName">文件名</param>
    /// <returns>不冲突的完整路径</returns>
    private static string GetUniqueDestinationPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < int.MaxValue; index++)
        {
            candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为 {fileName} 找到不冲突的文件名。");

        // 同名目录同样会让 File.Move 失败，因此一并跳过
        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    }

    #region 回收站

    /// <summary>
    /// 删除文件（放入回收站）
    /// </summary>
    private const uint FoDelete = 0x0003;

    /// <summary>
    /// 允许撤销，即放入回收站而不是直接删除
    /// </summary>
    private const ushort FofAllowUndo = 0x0040;

    /// <summary>
    /// 不显示确认对话框
    /// </summary>
    private const ushort FofNoConfirmation = 0x0010;

    /// <summary>
    /// 不显示进度对话框
    /// </summary>
    private const ushort FofSilent = 0x0004;

    /// <summary>
    /// 出错时不弹出界面
    /// </summary>
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>
    /// SHFileOperation 的参数结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    /// <summary>
    /// 调用 Windows Shell 执行文件操作
    /// </summary>
    /// <remarks>项目开启了 PublishAot，因此使用 DllImport 而不是 Microsoft.VisualBasic.FileIO。</remarks>
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>
    /// 将文件删除到回收站
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <exception cref="IOException">操作失败或文件仍然存在</exception>
    [SupportedOSPlatform("windows")]
    private static void SendToRecycleBin(string filePath)
    {
        // SHFileOperation 需要绝对路径，且 pFrom 是以 \0 分隔、再以 \0 结尾的列表
        var fullPath = Path.GetFullPath(filePath);
        var fileOp = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = fullPath + "\0\0",
            fFlags = unchecked((ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi))
        };

        var result = SHFileOperation(ref fileOp);
        if (result != 0)
        {
            throw new IOException($"移入回收站失败，SHFileOperation 返回 0x{result:X8}。");
        }

        if (fileOp.fAnyOperationsAborted != 0)
        {
            throw new IOException("移入回收站的操作被中止。");
        }

        // 兜底校验，避免 Shell 返回成功但文件并未被移走
        if (File.Exists(fullPath))
        {
            throw new IOException("移入回收站后文件仍然存在。");
        }
    }

    #endregion
}