using System.Text;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 全局错误日志静默记录器（捕获未处理异常并记录至本地文件，避免在控制台向普通用户直接输出惊悚的原始调用堆栈）
/// </summary>
public static class ErrorLogger
{
    /// <summary>
    /// 日志多线程写入与滚动轮转同步锁
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// 获取错误日志文件的持久化绝对路径（优先存储于 LocalApplicationData，发生异常时自动降级到程序根目录）
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoConvert", "logs");
            try
            {
                Directory.CreateDirectory(logDir);
                return Path.Combine(logDir, "error.log");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "error.log");
            }
        }
    }

    /// <summary>
    /// 记录异常详细信息（包含时间戳、上下文说明、多层内部异常与调用堆栈）到日志文件
    /// </summary>
    /// <remarks>
    /// 1. 采用单文件 5MB 滚动轮转策略（超过 5MB 自动重命名备份为 error.log.bak）；<br/>
    /// 2. 日志写入失败时静默吞掉，绝对不反向影响或崩溃命令行主流程。
    /// </remarks>
    /// <param name="ex">异常对象</param>
    /// <param name="contextDescription">发生异常的业务场景或上下文说明</param>
    /// <returns>实际写入的日志文件绝对路径</returns>
    public static string Log(Exception ex, string? contextDescription = null)
    {
        var path = LogFilePath;
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[时间]: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        if (!string.IsNullOrWhiteSpace(contextDescription))
        {
            sb.AppendLine($"[上下文]: {contextDescription}");
        }
        sb.AppendLine($"[异常类型]: {ex.GetType().FullName}");
        sb.AppendLine($"[异常消息]: {ex.Message}");
        if (ex.InnerException is not null)
        {
            sb.AppendLine($"[内部异常]: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
        }
        sb.AppendLine("[调用堆栈]:");
        sb.AppendLine(ex.StackTrace ?? "(无堆栈信息)");
        sb.AppendLine();

        lock (Gate)
        {
            try
            {
                // 控制单文件最大 5MB，超出时轮转备份
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                {
                    var backup = path + ".bak";
                    FileHelper.TryDeleteFile(backup);
                    File.Move(path, backup);
                }

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志记录失败时静默忽略，避免造成二次故障
            }
        }

        return path;
    }
}
