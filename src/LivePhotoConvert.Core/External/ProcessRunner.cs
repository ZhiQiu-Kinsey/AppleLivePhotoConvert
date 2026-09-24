using System.Diagnostics;
using System.Text;

namespace LivePhotoConvert.Core.External;

/// <param name="ExitCode">退出码</param>
/// <param name="StandardOutput">标准输出</param>
/// <param name="StandardError">标准错误</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>标准错误的最后一行非空内容，通常就是失败原因。</summary>
    public string ErrorSummary
    {
        get
        {
            ReadOnlySpan<char> last = default;
            foreach (var line in StandardError.AsSpan().EnumerateLines())
            {
                if (!line.Trim().IsEmpty)
                {
                    last = line.Trim();
                }
            }

            return last.IsEmpty ? "无错误输出" : last.ToString();
        }
    }
}

/// <summary>
/// 运行外部命令并收集输出。
/// </summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 运行命令直到退出；取消或超时时结束整个进程树。
    /// </summary>
    /// <remarks>
    /// 参数逐项传入 <see cref="ProcessStartInfo.ArgumentList"/>，由运行时负责转义；
    /// 标准输入立即关闭，防止 FFmpeg 等工具等待交互输入而挂起。
    /// </remarks>
    /// <exception cref="TimeoutException">超过 <paramref name="timeout"/> 仍未退出</exception>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        // 已取消时不再启动进程：批处理取消后排队中的条目会立即结束，而不是逐个启动再结束进程树
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        // 在等待退出前开始读取两路输出，避免管道缓冲区写满导致子进程阻塞
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await ((Task)Task.WhenAll(standardOutput, standardError)).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
            var limit = timeout ?? DefaultTimeout;
            var limitText = limit < TimeSpan.FromMinutes(1) ? $"{limit.TotalSeconds:F0} 秒" : $"{limit.TotalMinutes:F0} 分钟";
            throw new TimeoutException($"{Path.GetFileName(fileName)} 运行超过 {limitText}，已终止。");
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已退出
        }
    }
}
