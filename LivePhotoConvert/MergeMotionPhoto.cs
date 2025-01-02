using System.Diagnostics;

using ImageMagick;

using NReco.VideoConverter;

namespace LivePhotoConvert
{
    public class MergeMotionPhoto
    {
        private const string FfmpegPath = @".\";
        private static string TempDir = string.Empty;
        private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".png" };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".avi", ".mkv", ".flv" };

        /// <summary>
        /// 转换动态照片
        /// </summary>
        public static void Convert()
        {
            // 选择照片目录
            string? inputPath = UtilityHelp.SelectFolder("请选择照片目录");
            if (string.IsNullOrEmpty(inputPath))
            {
                Console.WriteLine("未选择照片目录，程序退出。");
                return;
            }

            // 选择输出目录
            string? outputPath = UtilityHelp.SelectFolder("请选择输出目录");
            if (string.IsNullOrEmpty(outputPath))
            {
                Console.WriteLine("未选择输出目录，程序退出。");
                return;
            }

            // 创建临时目录
            TempDir = Directory.CreateDirectory(Path.Combine(outputPath, "Temp")).FullName;

            // 获取照片和视频文件
            var photos = Directory.GetFiles(inputPath, "*", SearchOption.TopDirectoryOnly).Where(f => PhotoExtensions.Contains(Path.GetExtension(f))).ToList();
            var videos = Directory.GetFiles(inputPath, "*", SearchOption.TopDirectoryOnly).Where(f => VideoExtensions.Contains(Path.GetExtension(f))).ToList();

            // 匹配照片和视频
            var matchedGroups = photos.Join(videos, Path.GetFileNameWithoutExtension,
                                                    Path.GetFileNameWithoutExtension,
                                                    (photoPath, videoPath) => (photoPath, videoPath)).ToList();

            Console.WriteLine($"匹配到 {matchedGroups.Count} 组动态照片。");
            Console.WriteLine("是否开始转换？ (Y/N)");

            if (Console.ReadLine()?.Trim().ToLower() != "y")
            {
                Console.WriteLine("转换已取消。");
                return;
            }

            int totalTasks = matchedGroups.Count;
            int completedTasks = 0;

            matchedGroups.ForEach(group =>
            {
                try
                {
                    ProcessGroup(group.photoPath, group.videoPath, outputPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理 {group.photoPath} 时出错: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref completedTasks);
                    UtilityHelp.DrawProgressBar(completedTasks, totalTasks, Path.GetFileName(group.photoPath));
                }
            });

            // 删除临时目录
            Directory.Delete(TempDir, true);
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine($"成功转换{completedTasks}张动态照片，按任意键退出");
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
            UtilityHelp.InsertExifMetadata(outputFilePath, photoFilesize, mergedFilesize);

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
            // 将视频流写入照片末尾
            using var outfile = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var photo = new FileStream(photoPath, FileMode.Open, FileAccess.Read);
            long photoFilesize = photo.Length;
            using var video = new FileStream(videoPath, FileMode.Open, FileAccess.Read);
            photo.CopyTo(outfile);
            video.CopyTo(outfile);
            long mergedFilesize = outfile.Length;
            return (photoFilesize, mergedFilesize);
        }
    }
}