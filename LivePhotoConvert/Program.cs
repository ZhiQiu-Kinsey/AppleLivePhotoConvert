using System.Diagnostics;
using ImageMagick;

namespace LivePhotoConvert
{
    public class Program
    {
        private const string exiftoolPath = @".\ExifTool\ExifTool.exe";
        private static readonly object consoleLock = new ();
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

            // 获取照片和视频文件
            HashSet<string> photoExtensions = [".jpg", ".jpeg", ".heic"];
            HashSet<string> videoExtensions = [".mov", ".mp4"];
            List<string> photos = [.. Directory.GetFiles(photoDirectory, "*", SearchOption.TopDirectoryOnly).Where(f => photoExtensions.Contains(Path.GetExtension(f).ToLower()))];
            List<string> videos = [.. Directory.GetFiles(photoDirectory, "*", SearchOption.TopDirectoryOnly).Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLower()))];

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
            if (Console.ReadLine()?.TrimEnd().ToLower() != "y")
            {
                Console.WriteLine("转换已取消。");
                return;
            }

            int totalTasks = matchedGroups.Count;
            int completedTasks = 0;

            // 多线程处理
            Parallel.ForEach(matchedGroups, (group, loopState) =>
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

            Console.WriteLine("所有任务已完成。");
        }

        private static void ProcessGroup(string photoPath, string videoPath, string outputDirectory)
        {
            // 检查照片格式并转换HEIC为JPG
            string processedPhotoPath = photoPath;
            if (Path.GetExtension(photoPath).Equals(".heic", StringComparison.CurrentCultureIgnoreCase))
            {
                string tempDir = Path.Combine(outputDirectory, "Temp");
                Directory.CreateDirectory(tempDir);
                string jpgPath = Path.Combine(tempDir, Guid.NewGuid().ToString() + ".jpg");
                ConvertHeicToJpg(photoPath, jpgPath);
                processedPhotoPath = jpgPath;
            }

            // 生成输出路径
            string baseName = Path.GetFileNameWithoutExtension(photoPath);
            string outputFilePath = Path.Combine(outputDirectory, $"MVIMG_{baseName}.jpg");

            // 转换并合并文件
            ConvertAsync(processedPhotoPath, videoPath, outputFilePath);

            // 获取原照片的创建时间
            DateTime originalCreationTime = File.GetCreationTime(photoPath);
            // 获取原照片的最后修改时间
            DateTime originalWriteTime = File.GetLastWriteTime(photoPath);

            // 设置新图片的创建时间为原照片的创建时间
            File.SetCreationTime(outputFilePath, originalCreationTime);
            // 设置新图片的最后修改时间为原照片的最后修改时间
            File.SetLastWriteTime(outputFilePath, originalWriteTime);

            // 清理临时文件
            if (processedPhotoPath != photoPath)
            {
                File.Delete(processedPhotoPath);
            }
        }

        private static void ConvertHeicToJpg(string inputPath, string outputPath)
        {
            using MagickImage image = new(inputPath);
            image.Format = MagickFormat.Jpeg;
            image.Write(outputPath);
        }

        private static void ConvertAsync(string photoPath, string videoPath, string outputPath)
        {
            // 合并文件
            MergeFilesAsync(photoPath, videoPath, outputPath);

            // 获取文件大小并计算偏移量
            long photoFilesize = new FileInfo(photoPath).Length;
            long mergedFilesize = new FileInfo(outputPath).Length;
            long offset = mergedFilesize - photoFilesize;

            // 添加XMP元数据
            AddXmpMetadataAsync(outputPath, offset);
        }

        private static void MergeFilesAsync(string photoPath, string videoPath, string outputPath)
        {
            try
            {
                using FileStream outfile = new(outputPath, FileMode.Create, FileAccess.Write);
                using FileStream photo = new(photoPath, FileMode.Open, FileAccess.Read);
                using FileStream video = new(videoPath, FileMode.Open, FileAccess.Read);
                {
                    photo.CopyTo(outfile);
                    video.CopyTo(outfile);
                }
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private static void AddXmpMetadataAsync(string mergedPath, long offset)
        {
            string configPath = CreateExifToolConfigAsync();
            ProcessStartInfo startInfo = new()
            {
                FileName = exiftoolPath,
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

            using Process process = new() { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception("ExifTool 处理失败。");
            }
        }

        private static string CreateExifToolConfigAsync()
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
            lock (consoleLock)
            {
                // 计算进度并保留两位小数
                double progress = (double)completed / total;
                int filled = (int)(progress * barLength);
                // 处理文件名，超过 15 个字符时显示 "..."
                if (fileName.Length > 15)
                {
                    fileName = string.Concat("...", fileName.AsSpan(fileName.Length - 12, 12));
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