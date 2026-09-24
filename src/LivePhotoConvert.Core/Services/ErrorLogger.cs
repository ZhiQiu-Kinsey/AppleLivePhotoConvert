using System.Text;
using LivePhotoConvert.Core.Io;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 把未处理异常静默记录到本地日志文件，不向用户直接展示调用堆栈。
/// </summary>
public static class ErrorLogger
{
    /// <summary>单个日志文件的上限，超过后轮转为 <c>.bak</c>。</summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>
    /// 日志文件路径：优先放在本地应用数据目录，无法创建时退回程序目录。
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "LivePhotoConvert", "logs");
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
    /// 追加一条记录：时间、上下文、异常类型与消息、全部内部异常及调用堆栈。写入失败时静默忽略，不影响主流程。
    /// </summary>
    /// <param name="ex">异常</param>
    /// <param name="contextDescription">发生异常的业务场景</param>
    /// <returns>日志文件路径</returns>
    public static string Log(Exception ex, string? contextDescription = null) => Log(ex, contextDescription, LogFilePath);

    internal static string Log(Exception ex, string? contextDescription, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[时间]: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        if (!string.IsNullOrWhiteSpace(contextDescription))
        {
            sb.AppendLine($"[上下文]: {contextDescription}");
        }

        // ToString 包含每一层内部异常（含 AggregateException 的全部分支）的类型、消息与堆栈
        sb.AppendLine(ex.ToString());
        sb.AppendLine();

        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                {
                    var backup = path + ".bak";
                    FileHelper.TryDeleteFile(backup);
                    File.Move(path, backup);
                }

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去时不能再引发新的故障
            }
        }

        return path;
    }
}
