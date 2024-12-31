using System;
using System.Diagnostics;
using System.IO;

namespace LivePhotoConvert
{
    /// <summary>
    /// 拆分动态照片
    /// </summary>
    public class SplitMotionPhoto
    {
        /// <summary>
        /// 拆分动态照片
        /// </summary>
        public static void Split()
        {
            // 选择照片目录
            string? photoDirectory = UtilityHelp.SelectFolder("请选择照片目录");
            if (string.IsNullOrEmpty(photoDirectory))
            {
                Console.WriteLine("未选择照片目录，程序退出。");
                return;
            }

            // 选择输出目录
            string? outputDirectory = UtilityHelp.SelectFolder("请选择输出目录");
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.WriteLine("未选择输出目录，程序退出。");
                return;
            }

            // 获取所有.jpg文件
            var imageFiles = Directory.GetFiles(photoDirectory, "*.jpg");

            int totalTasks = imageFiles.Length;
            int completedTasks = 0;

            Console.WriteLine($"找到 {totalTasks} 个文件，是否开始拆分？ (Y/N)");

            if (Console.ReadLine()?.Trim().ToLower() != "y")
            {
                Console.WriteLine("拆分已取消。");
                return;
            }

            Parallel.ForEach(imageFiles, imagePath =>
            {
                string fileName = Path.GetFileName(imagePath);
                string outputJpgPath = Path.Combine(outputDirectory, fileName.Replace(".jpg", "_image.jpg"));
                string outputMp4Path = Path.Combine(outputDirectory, fileName.Replace(".jpg", "_video.mp4"));
                try
                {
                    // 第一步：获取MicroVideoOffset值
                    long offset = UtilityHelp.GetMicroVideoOffset(imagePath);

                    // 第二步：提取图片部分
                    ExtractData(imagePath, outputJpgPath, offset, true);

                    // 第三步：提取视频部分
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
            Console.WriteLine($"所有文件拆分完成！按任意键退出!");
            Console.ReadKey();
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
            long lengthMinusOffset = sourceStream.Length - offset;
            // 根据是否为图片确定起始位置
            long startPos = isImage ? 0 : lengthMinusOffset;
            // 根据是否为图片确定数据长度
            long dataLength = isImage ? lengthMinusOffset : offset;
            // 创建缓冲区
            byte[] buffer = new byte[dataLength];
            // 设置源文件流的位置
            sourceStream.Seek(startPos, SeekOrigin.Begin);
            // 从源文件流读取数据到缓冲区
            sourceStream.Read(buffer);
            // 将缓冲区数据写入输出文件流
            outputStream.Write(buffer);
        }
    }
}
