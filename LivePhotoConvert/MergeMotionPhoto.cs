using ImageMagick;

using NReco.VideoConverter;

namespace LivePhotoConvert;

/// <summary>
/// 动态照片合并
/// </summary>
public class MergeMotionPhoto
{
    private const string FfmpegPath = @".\";
    private static string TempDir = string.Empty;
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".heic", ".png" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".avi", ".mkv", ".flv" };
    private static readonly string[] PhotoExtensionPriority = [".heic", ".jpg", ".jpeg", ".png"];
    private static readonly string[] VideoExtensionPriority = [".mov", ".mp4", ".avi", ".mkv", ".flv"];

    /// <summary>
    /// 转换动态照片
    /// </summary>
    public static void Convert()
    {
        UtilityHelp.Print("合成动态照片");
        // 选择照片目录
        var inputPath = UtilityHelp.SelectFolder("请选择输入目录");
        // 选择输出目录
        var outputPath = UtilityHelp.SelectFolder("请选择输出目录");
        // 创建临时目录
        TempDir = Directory.CreateDirectory(Path.Combine(outputPath, "Temp")).FullName;

        // 获取照片和视频文件
        var allFiles = Directory.GetFiles(inputPath, "*", SearchOption.TopDirectoryOnly);
        var photos = allFiles.Where(f => PhotoExtensions.Contains(Path.GetExtension(f))).ToList();
        var videos = allFiles.Where(f => VideoExtensions.Contains(Path.GetExtension(f))).ToList();
        // 同名多格式（如 IMG_0001.heic 与 IMG_0001.jpg）只保留优先级最高的一个，避免同一张照片被匹配成多组
        var uniquePhotos = PickPreferredByName(photos, PhotoExtensionPriority);
        var uniqueVideos = PickPreferredByName(videos, VideoExtensionPriority);

        // 匹配照片和视频
        var matchedGroups = uniquePhotos.Join(uniqueVideos, GetNameKey, GetNameKey,
                                              (photoPath, videoPath) => (photoPath, videoPath),
                                              StringComparer.OrdinalIgnoreCase).ToList();

        // 统计未匹配的文件，它们在任何选项下都不会被处理
        var matchedNames = new HashSet<string>(matchedGroups.Select(g => GetNameKey(g.photoPath)), StringComparer.OrdinalIgnoreCase);
        var matchedFiles = new HashSet<string>(matchedGroups.SelectMany(g => new[] { g.photoPath, g.videoPath }), StringComparer.OrdinalIgnoreCase);
        var unmatchedPhotos = photos.Count(f => !matchedNames.Contains(GetNameKey(f)));
        var unmatchedVideos = videos.Count(f => !matchedNames.Contains(GetNameKey(f)));
        // 同名但未被选中的备选格式，只清理实际参与合成的那个文件，这些一律保留
        var skippedDuplicates = photos.Concat(videos).Count(f => matchedNames.Contains(GetNameKey(f)) && !matchedFiles.Contains(f));

        Console.WriteLine($"匹配到 {matchedGroups.Count} 组动态照片。");
        Console.WriteLine($"未匹配的照片 {unmatchedPhotos} 个，未匹配的视频 {unmatchedVideos} 个，均不会被合成或清理。");
        if (skippedDuplicates > 0)
        {
            Console.WriteLine($"另有 {skippedDuplicates} 个同名备选格式文件未参与合成，也不会被清理。");
        }

        // 合成前确认如何处理已匹配的原始文件
        var sourceAction = UtilityHelp.AskSourceFileAction();

        Console.WriteLine("是否开始转换？ (Y/N)");

        if (Console.ReadLine()?.Trim().ToLower() != "y")
        {
            Console.WriteLine("转换已取消。");
            Environment.Exit(0);
        }

        UtilityHelp.Print("正在合成");
        var totalTasks = matchedGroups.Count;
        var completedTasks = 0;
        var succeededTasks = 0;
        var cleanedFiles = 0;
        var cleanupFailures = new List<string>();
        matchedGroups.ForEach(group =>
        {
            try
            {
                ProcessGroup(group.photoPath, group.videoPath, outputPath);
                Interlocked.Increment(ref succeededTasks);
                // 只有合成成功并通过校验的这一组才会被清理，未匹配的文件绝不会进入这里
                cleanedFiles += UtilityHelp.CleanupSourceFiles([group.photoPath, group.videoPath], sourceAction, inputPath, cleanupFailures);
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
        Console.WriteLine($"成功转换 {succeededTasks}/{totalTasks} 张动态照片。");
        if (sourceAction != SourceFileAction.Keep)
        {
            Console.WriteLine($"已{DescribeAction(sourceAction)}原始文件 {cleanedFiles} 个。");
        }

        if (cleanupFailures.Count > 0)
        {
            Console.WriteLine($"以下 {cleanupFailures.Count} 个原始文件清理失败，请手动处理（动态照片已合成成功，不受影响）：");
            cleanupFailures.ForEach(failure => Console.WriteLine($"  {failure}"));
        }

        Console.WriteLine("按任意键退出。");
        Console.ReadKey();
        Environment.Exit(0);
    }

    /// <summary>
    /// 获取用于匹配的文件名（不含扩展名）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>不含扩展名的文件名</returns>
    private static string GetNameKey(string filePath) => Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;

    /// <summary>
    /// 同名文件按扩展名优先级只保留一个
    /// </summary>
    /// <param name="files">文件列表</param>
    /// <param name="extensionPriority">扩展名优先级，越靠前优先级越高</param>
    /// <returns>去重后的文件列表</returns>
    private static List<string> PickPreferredByName(List<string> files, string[] extensionPriority)
    {
        return files.GroupBy(GetNameKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(GetExtensionRank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).First())
                    .ToList();

        int GetExtensionRank(string filePath)
        {
            var rank = Array.FindIndex(extensionPriority, e => e.Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase));
            return rank < 0 ? extensionPriority.Length : rank;
        }
    }

    /// <summary>
    /// 描述原始文件的处理方式
    /// </summary>
    /// <param name="action">处理方式</param>
    /// <returns>描述文本</returns>
    private static string DescribeAction(SourceFileAction action) => action switch
    {
        SourceFileAction.Move => $"移动到 \"{UtilityHelp.MergedFolderName}\" 子文件夹",
        SourceFileAction.Recycle => "删除到回收站",
        SourceFileAction.Delete => "永久删除",
        _ => "保留"
    };

    /// <summary>
    /// 处理照片和视频
    /// </summary>
    /// <param name="photoPath">照片路径</param>
    /// <param name="videoPath">视频路径</param>
    /// <param name="outputDirectory">输出目录</param>
    private static void ProcessGroup(string photoPath, string videoPath, string outputDirectory)
    {
        // 检查照片格式并转换HEIC为JPG
        var processedPhotoPath = photoPath;
        if (Path.GetExtension(photoPath).Equals(".heic", StringComparison.OrdinalIgnoreCase))
        {
            processedPhotoPath = ConvertHeicToJpg(photoPath);
        }

        // 检查视频格式并转换MOV为MP4
        var processedVideoPath = videoPath;
        if (Path.GetExtension(videoPath).Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            processedVideoPath = ConvertMovToMp4(videoPath);
        }

        // 生成输出路径
        var baseName = Path.GetFileNameWithoutExtension(photoPath);
        var outputFilePath = Path.Combine(outputDirectory, $"MVIMG_{baseName}.jpg");

        // 合并文件
        (var photoFilesize, var mergedFilesize) = MergeFiles(processedPhotoPath, processedVideoPath, outputFilePath);
        // 添加XMP元数据
        UtilityHelp.InsertExifMetadata(outputFilePath, photoFilesize, mergedFilesize);

        // 写入元数据后校验输出文件，长度不足说明尾部的视频数据丢失，此时抛异常以保留原始文件
        var finalFilesize = new FileInfo(outputFilePath).Length;
        if (finalFilesize < mergedFilesize)
        {
            throw new InvalidDataException($"合成校验失败：输出文件 {finalFilesize} 字节，小于照片与视频之和 {mergedFilesize} 字节，视频数据可能已丢失。");
        }

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
    /// <param name="photoPath">照片路径</param>
    /// <returns>转换后的JPG文件路径</returns>
    private static string ConvertHeicToJpg(string photoPath)
    {
        var outputPath = Path.Combine(TempDir, Guid.NewGuid() + ".jpg");
        using MagickImage image = new(photoPath);
        image.Format = MagickFormat.Jpeg;
        image.Write(outputPath);
        return outputPath;
    }

    /// <summary>
    /// 将MOV转换为MP4
    /// </summary>
    /// <param name="videoPath">视频路径</param>
    /// <returns>转换后的MP4文件路径</returns>
    private static string ConvertMovToMp4(string videoPath)
    {
        var outputPath = Path.Combine(TempDir, Guid.NewGuid() + ".mp4");
        var converter = new FFMpegConverter
        {
            FFMpegToolPath = FfmpegPath,
        };
        converter.ConvertMedia(videoPath, null, outputPath, "mp4", new ConvertSettings
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
    /// <param name="photoPath">照片路径</param>
    /// <param name="videoPath">视频路径</param>
    /// <param name="outputPath">输出路径</param>
    /// <returns>照片和视频文件的大小</returns>
    private static (long, long) MergeFiles(string photoPath, string videoPath, string outputPath)
    {
        // 将视频流写入照片末尾
        using var outfile = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var photo = new FileStream(photoPath, FileMode.Open, FileAccess.Read);
        var photoFilesize = photo.Length;
        using var video = new FileStream(videoPath, FileMode.Open, FileAccess.Read);
        // 由输入长度精确计算，不读取 outfile.Length，避免受输出流缓冲影响
        var mergedFilesize = photoFilesize + video.Length;
        photo.CopyTo(outfile);
        video.CopyTo(outfile);
        return (photoFilesize, mergedFilesize);
    }
}