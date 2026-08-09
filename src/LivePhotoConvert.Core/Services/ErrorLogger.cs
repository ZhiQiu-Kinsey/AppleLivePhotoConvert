using System.Text;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 错误日志静默记录器
/// </summary>
public static class ErrorLogger
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// 获取错误日志文件路径
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LivePhotoConvert",
                "logs");

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
    /// 记录异常到日志文件，绝不在控制台直接抛出原生堆栈报错
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="contextDescription">发生异常的上下文说明</param>
    /// <returns>写入的日志文件路径</returns>
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
                // 控制单文件最大 5MB
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                {
                    var backup = path + ".bak";
                    File.Delete(backup);
                    File.Move(path, backup);
                }

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志记录失败时静默吞掉，不影响主流程
            }
        }

        return path;
    }
}
