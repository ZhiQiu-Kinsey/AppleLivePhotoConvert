namespace LivePhotoConvert;

/// <summary>
/// 动态照片拆分
/// </summary>
public class SplitMotionPhoto
{
    /// <summary>
    /// 拆分动态照片
    /// </summary>
    public static void Split()
    {
        UtilityHelp.Print("拆分动态照片");
        // 选择照片目录
        var photoDirectory = UtilityHelp.SelectFolder("请选择输入目录");
        // 选择输出目录
        var outputDirectory = UtilityHelp.SelectFolder("请选择输出目录");
        // 获取所有.jpg文件
        var imageFiles = Directory.GetFiles(photoDirectory, "*.jpg");
        var totalTasks = imageFiles.Length;
        var completedTasks = 0;
        Console.WriteLine($"找到 {totalTasks} 个文件，是否开始拆分？ (Y/N)");
        if (Console.ReadLine()?.Trim().ToLower() != "y")
        {
            Console.WriteLine("拆分已取消。");
            Environment.Exit(0);
        }

        UtilityHelp.Print("正在拆分");
        Parallel.ForEach(imageFiles, imagePath =>
        {
            var fileName = Path.GetFileName(imagePath);
            var outputJpgPath = Path.Combine(outputDirectory, fileName.Replace(".jpg", "_01.jpg"));
            var outputMp4Path = Path.Combine(outputDirectory, fileName.Replace(".jpg", "_01.mp4"));
            try
            {
                // 获取MicroVideoOffset值
                var offset = UtilityHelp.GetMicroVideoOffset(imagePath);
                // 提取图片部分
                ExtractData(imagePath, outputJpgPath, offset, true);
                // 删除元数据
                UtilityHelp.RemoveXmpAndExifTags(outputJpgPath);
                // 提取视频部分
                ExtractData(imagePath, outputMp4Path, offset, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理文件 {fileName} 时发生错误: {ex.Message}");
            }
            finally
            {
                Interlocked.Increment(ref completedTasks);
                UtilityHelp.DrawProgressBar(completedTasks, totalTasks, fileName);
            }
        });

        Console.WriteLine(Environment.NewLine);
        Console.WriteLine("所有文件拆分完成！按任意键退出!");
        Console.ReadKey();
        Environment.Exit(0);
    }

    /// <summary>
    /// 提取图片或视频
    /// </summary>
    /// <param name="imagePath">输入文件路径</param>
    /// <param name="outputPath">输出文件路径</param>
    /// <param name="offset">偏移量</param>
    /// <param name="isImage">是否为图片</param>
    public static void ExtractData(string imagePath, string outputPath, long offset, bool isImage)
    {
        using FileStream sourceStream = new(imagePath, FileMode.Open, FileAccess.Read);
        using FileStream outputStream = new(outputPath, FileMode.Create, FileAccess.Write);
        // 计算源文件长度减去偏移量的值
        var lengthMinusOffset = sourceStream.Length - offset;
        // 根据是否为图片确定起始位置
        var startPos = isImage ? 0 : lengthMinusOffset;
        // 根据是否为图片确定数据长度
        var dataLength = isImage ? lengthMinusOffset : offset;
        // 创建缓冲区
        var buffer = new byte[dataLength];
        // 设置源文件流的位置
        sourceStream.Seek(startPos, SeekOrigin.Begin);
        // 从源文件流读取数据到缓冲区
        sourceStream.ReadExactly(buffer);
        // 将缓冲区数据写入输出文件流
        outputStream.Write(buffer);
    }
}