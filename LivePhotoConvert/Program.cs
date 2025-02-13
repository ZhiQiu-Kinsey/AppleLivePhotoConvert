namespace LivePhotoConvert;

public class Program
{
    public static void Main()
    {
        Console.Title = "动态照片转换工具";
        UtilityHelp.Print("欢迎使用动态照片工具箱");
        // 显示操作选项菜单
        Console.WriteLine("请选择操作：");
        Console.WriteLine("1. 合成动态照片");
        Console.WriteLine("2. 拆分动态照片");
        Console.WriteLine("3. 合成iPhone动态照片");
        Console.WriteLine("4. 退出");
        var choice = Console.ReadLine();
        Console.Clear();
        // 根据用户选择的操作执行不同的功能
        switch (choice)
        {
            case "1":
                // 合成动态照片
                MergeMotionPhoto.Convert();
                break;
            case "2":
                // 拆分动态照片
                SplitMotionPhoto.Split();
                break;
            case "3":
                // 合成iPhone动态照片
                // TODO
                break;
            default:
                Console.WriteLine("程序退出。");
                Environment.Exit(0);
                break;
        }
    }
}