using System;
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
            // 显示操作选项菜单
            Console.WriteLine("请选择操作：");
            Console.WriteLine("1. 合成动态照片");
            Console.WriteLine("2. 拆分动态照片");

            string? choice = Console.ReadLine();

            // 根据用户选择的操作执行不同的功能
            if (choice == "1")
            {
                // 合成动态照片
                MergeMotionPhoto.Convert();
            }
            else if (choice == "2")
            {
                // 拆分动态照片
                SplitMotionPhoto.Split();
            }
            else
            {
                Console.WriteLine("无效的选项，程序退出。");
            }
        }
    }
}
