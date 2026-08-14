using System.Diagnostics;
using System.Text;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// 外部进程的执行结果数据记录
/// </summary>
/// <param name="ExitCode">进程退出状态码（0 代表成功）</param>
/// <param name="StandardOutput">标准输出文本内容</param>
/// <param name="StandardError">标准错误文本内容</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// 是否执行成功（退出码为 0）
    /// </summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 高性能轻量级外部进程执行与生命周期调度器（基于 .NET 10 原生异步管道与进程树安全回收）
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// 异步执行外部进程并等待其运行结束，实时捕获标准输出与标准错误
    /// </summary>
    /// <remarks>
    /// 1. 采用 <see cref="ProcessStartInfo.ArgumentList"/> 进行参数传递，由运行时原生负责特殊字符转义，杜绝路径引号与空格注入风险；<br/>
    /// 2. 在调用 <see cref="Process.WaitForExitAsync"/> 之前立即挂起标准流的异步读取任务，防止输出缓冲区满导致子进程管道阻塞死锁；<br/>
    /// 3. 当触发取消令牌时，强制递归销毁整个进程树（<c>entireProcessTree: true</c>），杜绝孤儿进程驻留。
    /// </remarks>
    /// <param name="fileName">可执行文件绝对路径</param>
    /// <param name="arguments">参数序列</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含退出码、标准输出和标准错误的 <see cref="ProcessResult"/></returns>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

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

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 异步并行读取双输出流，防止管道缓冲区死锁
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// 安全销毁子进程及其关联的全部派生进程树
    /// </summary>
    /// <param name="process">目标进程实例</param>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能在尝试终止时已自行退出，安全忽略
        }
    }
}

