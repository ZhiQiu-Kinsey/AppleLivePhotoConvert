using System.Diagnostics;
using System.Reflection;

using ImageMagick;

using NReco.VideoConverter;

namespace LivePhotoConvert
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // 选择照片目录
            string? photoDirectory = MergeMotionPhoto.SelectFolder("请选择照片目录");
            if (string.IsNullOrEmpty(photoDirectory))
            {
                Console.WriteLine("未选择照片目录，程序退出。");
                return;
            }

            // 选择输出目录
            string? outputDirectory = MergeMotionPhoto.SelectFolder("请选择输出目录");
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Console.WriteLine("未选择输出目录，程序退出。");
                return;
            }
            MergeMotionPhoto.Convert(photoDirectory, outputDirectory);
        }
    }
}
