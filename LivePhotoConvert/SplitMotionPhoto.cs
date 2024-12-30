using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        /// <param name="photoDirectory">照片目录</param>
        /// <param name="outputDirectory">输出目录</param>
        public static void Split(string photoDirectory, string outputDirectory)
        {
            // TODO 拆分动态照片
            // 获取照片文件
            string[] photoFiles =Directory.GetFiles(photoDirectory, "*.jpg");
            if (photoFiles.Length == 0)
            {
                Console.WriteLine("未找到照片文件，程序退出。");
                return;
            }
        }
    }
}
