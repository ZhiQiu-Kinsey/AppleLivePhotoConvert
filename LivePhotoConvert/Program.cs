using System.Diagnostics;

using ImageMagick;

using NReco.VideoConverter;

namespace LivePhotoConvert
{
    public class Program
    {
        private const string ExifToolPath = @".\ExifTool\ExifTool.exe";
        private const string FfmpegPath = @".\";
        private static string TempDir = string.Empty;
        private static readonly object ConsoleLock = new();

        public static void Main(string[] args)
        {
            // 选择照片目录
            string? photoDirectory = SelectFolder("请选择照片目录");
            if (string.IsNullOrEmpty(photoDirectory))
            {
                Console.WriteLine("未选择照片目录，程序退出。");
                return;
            }

            // 选择输出目录
            string? outputDirectory = SelectFolder("请选择输出目录");
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.WriteLine("未选择输出目录，程序退出。");
                return;
            }

            TempDir = Path.Combine(outputDirectory, "Temp");
            // 创建临时目录
            Directory.CreateDirectory(TempDir);

            // 获取照片和视频文件
            HashSet<string> photoExtensions = [".jpg", ".jpeg", ".heic", ".JPG", ".JPEG", ".HEIC"];
            HashSet<string> videoExtensions = [".mov", ".mp4", ".MOV", ".MP4"];

            // 获取照片和视频文件
            var photos = Directory.GetFiles(photoDirectory, "*", SearchOption.TopDirectoryOnly).Where(f => photoExtensions.Contains(Path.GetExtension(f))).ToList();
            var videos = Directory.GetFiles(photoDirectory, "*", SearchOption.TopDirectoryOnly).Where(f => videoExtensions.Contains(Path.GetExtension(f))).ToList();

            // 匹配照片和视频
            var matchedGroups = new List<Tuple<string, string>>();
            foreach (var photo in photos)
            {
                string baseName = Path.GetFileNameWithoutExtension(photo);
                var video = videos.FirstOrDefault(v => Path.GetFileNameWithoutExtension(v) == baseName);
                if (video != null)
                {
                    matchedGroups.Add(Tuple.Create(photo, video));
                }
            }

            Console.WriteLine($"匹配到 {matchedGroups.Count} 组动态照片。");
            Console.WriteLine("是否开始转换？ (Y/N)");

            if (Console.ReadLine()?.Trim().ToLower() != "y")
            {
                Console.WriteLine("转换已取消。");
                return;
            }

            int totalTasks = matchedGroups.Count;
            int completedTasks = 0;

            // 使用 Parallel.ForEach 进行并行处理
            Parallel.ForEach(matchedGroups, group =>
            {
                try
                {
                    ProcessGroup(group.Item1, group.Item2, outputDirectory);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理 {group.Item1} 时出错: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref completedTasks);
                    DrawProgressBar(completedTasks, totalTasks, Path.GetFileName(group.Item1));
                }
            });

            // 删除临时目录
            Directory.Delete(TempDir, true);
            Console.ReadKey();
        }

        /// <summary>
        /// 处理照片和视频
        /// </summary>
        /// <param name="photoPath"></param>
        /// <param name="videoPath"></param>
        /// <param name="outputDirectory"></param>
        private static void ProcessGroup(string photoPath, string videoPath, string outputDirectory)
        {
            // 检查照片格式并转换HEIC为JPG
            string processedPhotoPath = photoPath;
            if (Path.GetExtension(photoPath).Equals(".heic", StringComparison.OrdinalIgnoreCase))
            {
                processedPhotoPath = ConvertHeicToJpg(photoPath);
            }

            // 检查视频格式并转换MOV为MP4
            string processedVideoPath = videoPath;
            if (Path.GetExtension(videoPath).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            {
                processedVideoPath = ConvertMovToMp4(videoPath);
            }

            // 生成输出路径
            string baseName = Path.GetFileNameWithoutExtension(photoPath);
            string outputFilePath = Path.Combine(outputDirectory, $"MVIMG_{baseName}.jpg");

            // 合并文件
            (long photoFilesize, long mergedFilesize) = MergeFiles(processedPhotoPath, processedVideoPath, outputFilePath);
            // 添加XMP元数据
            AddXmpMetadata(outputFilePath, photoFilesize, mergedFilesize);

            // 设置新图片的创建时间为原照片的创建时间
            File.SetCreationTime(outputFilePath, File.GetCreationTime(photoPath));
            // 设置新图片的最后修改时间为原照片的最后修改时间
            File.SetLastWriteTime(outputFilePath, File.GetLastWriteTime(photoPath));

            // 清理临时文件
            if (processedPhotoPath != photoPath)
            {
                File.Delete(processedPhotoPath);
            }
            if (processedVideoPath != videoPath)
            {
                File.Delete(processedVideoPath);
            }
        }

        /// <summary>
        /// 将HEIC转换为JPG
        /// </summary>
        /// <param name="inputPath"></param>
        /// <param name="outputPath"></param>
        private static string ConvertHeicToJpg(string inputPath)
        {
            string outputPath = Path.Combine(TempDir, Guid.NewGuid() + ".jpg");
            using MagickImage image = new(inputPath);
            image.Format = MagickFormat.Jpeg;
            image.Write(outputPath);
            return outputPath;
        }

        /// <summary>
        /// 将MOV转换为MP4
        /// </summary>
        /// <param name="inputPath"></param>
        /// <param name="outputPath"></param>
        private static string ConvertMovToMp4(string inputPath)
        {
            string outputPath = Path.Combine(TempDir, Guid.NewGuid() + ".mp4");
            var converter = new FFMpegConverter
            {
                FFMpegToolPath = FfmpegPath,
            };
            converter.ConvertMedia(inputPath, null, outputPath, "mp4", new ConvertSettings
            {
                // AMD显卡加速 (需要安装ROCM,并且质量会下降)
                //VideoCodec = "h264_amf"
                // Nvidia显卡加速 (未测试)
                //VideoCodec = "h264_nvenc"
                // Intel显卡加速 (未测试)
                //VideoCodec = "h264_qsv"
            });
            return outputPath;
        }

        /// <summary>
        /// 合并照片和视频
        /// </summary>
        /// <param name="photoPath"></param>
        /// <param name="videoPath"></param>
        /// <param name="outputPath"></param>
        private static (long, long) MergeFiles(string photoPath, string videoPath, string outputPath)
        {
            long photoFilesize;
            long mergedFilesize;
            // 将视频流写入照片末尾
            using var outfile = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var photo = new FileStream(photoPath, FileMode.Open, FileAccess.Read);
            photoFilesize = photo.Length;
            using var video = new FileStream(videoPath, FileMode.Open, FileAccess.Read);
            {
                photo.CopyTo(outfile);
                video.CopyTo(outfile);
            }
            mergedFilesize = outfile.Length;
            return (photoFilesize, mergedFilesize);
        }

        /// <summary>
        /// 添加XMP元数据
        /// </summary>
        /// <param name="mergedPath"></param>
        /// <param name="offset"></param>
        /// <exception cref="Exception"></exception>
        private static void AddXmpMetadata(string mergedPath, long photoFilesize,long mergedFilesize)
        {
            // 计算偏移量
            long offset = mergedFilesize - photoFilesize;
            string configPath = CreateExifToolConfig();
            ProcessStartInfo startInfo = new()
            {
                FileName = ExifToolPath,
                Arguments = $"-config \"{configPath}\" " +
                            $"-XMP-GCamera:MicroVideo=1 " +
                            $"-XMP-GCamera:MicroVideoVersion=1 " +
                            $"-XMP-GCamera:MicroVideoOffset={offset} " +
                            $"-XMP-GCamera:MicroVideoPresentationTimestampUs={(offset / 2)} " +
                            $"-MicroVideo=1 " +
                            $"-overwrite_original \"{mergedPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception("ExifTool 处理失败。");
            }
        }

        /// <summary>
        /// 创建ExifTool配置文件
        /// </summary>
        /// <returns></returns>
        private static string CreateExifToolConfig()
        {
            string configFile = "custom_exiftool.config";
            if (File.Exists(configFile))
            {
                return configFile;
            }
            string configContent = @"%Image::ExifTool::UserDefined = (
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
                                    );";
            File.WriteAllText(configFile, configContent);
            return configFile;
        }

        private static string? SelectFolder(string message)
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
        /// <param name="completed"></param>
        /// <param name="total"></param>
        /// <param name="barLength"></param>
        private static void DrawProgressBar(int completed, int total, string fileName, int barLength = 70)
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
}
