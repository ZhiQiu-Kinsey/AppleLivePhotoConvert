using System.Diagnostics;
using System.Text;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部进程的执行结果
/// </summary>
/// <param name="ExitCode">退出码</param>
/// <param name="StandardOutput">标准输出</param>
/// <param name="StandardError">标准错误</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// 是否执行成功
    /// </summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 一次性执行外部进程
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// 执行外部进程并等待结束
    /// </summary>
    /// <remarks>
    /// 参数通过 ArgumentList 传递，由运行时负责转义，避免路径中的引号或空格破坏命令行。
    /// 输出流在等待退出之前就开始异步读取，避免管道缓冲区写满导致的死锁。
    /// </remarks>
    /// <param name="fileName">可执行文件路径</param>
    /// <param name="arguments">参数列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>执行结果</returns>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        // 先挂上读取任务再等待退出，顺序不能颠倒
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    /// <summary>
    /// 尽力终止进程，失败时忽略
    /// </summary>
    /// <param name="process">目标进程</param>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能刚好自行退出，无需处理
        }
    }
}
