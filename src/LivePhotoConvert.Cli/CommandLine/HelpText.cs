namespace LivePhotoConvert.Cli.CommandLine;

/// <summary>
/// 命令行帮助
/// </summary>
static class HelpText
{
    /// <summary>
    /// 打印用法说明
    /// </summary>
    public static void Print()
    {
        Console.WriteLine("""
                          动态照片工具箱 —— 在苹果实况照片与安卓动态照片之间转换

                          用法:
                            LivePhotoConvert                      不带参数启动，进入交互菜单
                            LivePhotoConvert merge  [选项]        合成动态照片
                            LivePhotoConvert split  [选项]        拆分动态照片
                            LivePhotoConvert tools  [选项]        检查并下载外部工具 (ExifTool / FFmpeg)
                            LivePhotoConvert --help               显示本帮助
                            LivePhotoConvert --version            显示版本

                          通用选项:
                            -i, --input <目录>       输入目录，省略时会弹出文件夹选择框
                            -o, --output <目录>      输出目录，省略时会弹出文件夹选择框
                            -p, --parallel <数量>    并行处理的文件数，默认按 CPU 核心数推算
                                --overwrite          输出目录存在同名文件时直接覆盖，默认追加 _1、_2 后缀
                                --auto-download      若缺少 ExifTool 或 FFmpeg 则自动下载安装
                                --mirror <前缀>      指定 GitHub 加速镜像前缀（如 https://ghfast.top/）
                                --exiftool <路径>    指定 ExifTool 可执行文件，默认在程序目录、tools 和 PATH 中查找
                            -y, --yes                跳过开始前的确认，便于脚本调用

                          merge 专用选项:
                            -s, --source-action <方式>   合成成功后如何处理【已匹配】的原始文件:
                                                           keep     保留（默认）
                                                           move     移动到输入目录下的"已合成"子文件夹
                                                           recycle  删除到回收站（仅 Windows）
                                                           delete   永久删除，不可恢复
                                --strict                 用苹果 Content Identifier 校验照片与视频确实
                                                         来自同一张实况照片，更安全但更慢
                                --ffmpeg <路径>          指定 FFmpeg 可执行文件

                          split 专用选项:
                            -f, --format <格式>          拆分输出的目标格式:
                                                           android  标准安卓格式（.jpg/.heic + .mp4，无损切片，默认）
                                                           apple    苹果实况照片（.jpg/.heic + .mov，写入 Live Photo 元数据）

                          说明:
                            未匹配的照片和视频（含未配对的长视频）在任何选项下都不会被移动或删除。
                            使用 --source-action delete 前请确认输出结果无误，该操作不可恢复。

                          示例:
                            LivePhotoConvert merge -i D:\照片 -o D:\动态照片
                            LivePhotoConvert merge -i D:\照片 -o D:\动态照片 -s move -y --auto-download
                            LivePhotoConvert split -i D:\动态照片 -o D:\拆分结果
                            LivePhotoConvert split -i D:\动态照片 -o D:\苹果实况 -f apple
                            LivePhotoConvert tools --mirror https://ghfast.top/
                          """);
    }
}
