using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace LivePhotoConvert.Core.External;

/// <summary>
/// ExifTool 一次调用的输出
/// </summary>
/// <param name="StandardOutput">标准输出</param>
/// <param name="StandardError">标准错误</param>
sealed record ExifToolResponse(string StandardOutput, string StandardError);

/// <summary>
/// 常驻的 ExifTool 进程
/// </summary>
/// <remarks>
/// ExifTool 是打包的 Perl 程序，每次启动需要数百毫秒。批量处理时进程启动会成为主要开销，
/// 因此使用 -stay_open 模式复用同一个进程，通过标准输入逐条下发命令。
/// 该进程一次只能处理一条命令，调用已用信号量串行化。
/// </remarks>
sealed class ExifToolSession(string executablePath, string configPath) : IAsyncDisposable
{
    /// <summary>
    /// 单条命令的超时时间，超时后重启进程
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 退出时等待进程自行结束的时间
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private Channel<string>? _standardOutput;
    private Channel<string>? _standardError;
    private int _sequence;
    private bool _disposed;

    /// <summary>
    /// 执行一条 ExifTool 命令
    /// </summary>
    /// <param name="arguments">参数列表，每个元素对应命令行上的一个参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>命令输出</returns>
    /// <exception cref="TimeoutException">ExifTool 在超时时间内没有响应</exception>
    /// <exception cref="IOException">ExifTool 进程意外退出</exception>
    public async Task<ExifToolResponse> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            var sequence = ++_sequence;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                await WriteCommandAsync(arguments, sequence, timeout.Token);
                var standardOutput = await ReadUntilAsync(_standardOutput!.Reader, $"{{ready{sequence}}}", timeout.Token);
                var standardError = await ReadUntilAsync(_standardError!.Reader, $"{{readyerr{sequence}}}", timeout.Token);
                return new ExifToolResponse(standardOutput, standardError);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 命令中断后管道里可能残留未读完的输出，进程状态不可知，结束掉让下次调用重新启动
                StopProcess();
                throw new TimeoutException($"ExifTool 在 {CommandTimeout.TotalSeconds} 秒内没有响应。");
            }
            catch
            {
                StopProcess();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 确保 ExifTool 进程处于可用状态
    /// </summary>
    private void EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        StopProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 不带 BOM 的 UTF-8：Encoding.UTF8 默认会在流开头写入 BOM (0xEF 0xBB 0xBF)，
            // ExifTool 通过 stdin 逐行读取参数时，BOM 会粘在第一条命令的第一个参数前面，
            // 导致该参数被误认为文件名而报 "File not found"
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // -config 必须严格作为 ExifTool 启动参数的绝对第 1 项，否则会被 ExifTool 忽略
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-charset");
        startInfo.ArgumentList.Add("filename=UTF8");
        startInfo.ArgumentList.Add("-stay_open");
        startInfo.ArgumentList.Add("True");
        // 从标准输入读取参数
        startInfo.ArgumentList.Add("-@");
        startInfo.ArgumentList.Add("-");
        var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = CreateChannel();
        var standardError = CreateChannel();
        _ = PumpAsync(process.StandardOutput, standardOutput.Writer);
        _ = PumpAsync(process.StandardError, standardError.Writer);
        _process = process;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    /// <summary>
    /// 创建承载输出行的通道
    /// </summary>
    /// <returns>通道</returns>
    private static Channel<string> CreateChannel() => Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// 把命令写入 ExifTool 的标准输入
    /// </summary>
    /// <param name="arguments">参数列表</param>
    /// <param name="sequence">本次命令的序号，用于匹配结束标记</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task WriteCommandAsync(IReadOnlyList<string> arguments, int sequence, CancellationToken cancellationToken)
    {
        var input = _process!.StandardInput;
        foreach (var argument in arguments)
        {
            await input.WriteLineAsync(argument.AsMemory(), cancellationToken);
        }

        // ExifTool 只会在标准输出打印结束标记，这里用 -echo4 让标准错误也带上标记，
        // 否则读取标准错误时无法判断本次命令的输出是否已经结束
        await input.WriteLineAsync("-echo4".AsMemory(), cancellationToken);
        await input.WriteLineAsync($"{{readyerr{sequence}}}".AsMemory(), cancellationToken);
        await input.WriteLineAsync($"-execute{sequence}".AsMemory(), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 持续把进程输出的每一行送入通道
    /// </summary>
    /// <param name="reader">流读取器</param>
    /// <param name="writer">通道写入端</param>
    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await writer.WriteAsync(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // 进程退出时流会被关闭，属于正常现象
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }

        writer.TryComplete();
    }

    /// <summary>
    /// 从通道读取输出，直到遇到匹配的结束标记
    /// </summary>
    /// <param name="reader">通道读取端</param>
    /// <param name="marker">结束标记</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>标记之前的所有输出行拼接成的字符串</returns>
    /// <exception cref="IOException">通道已关闭但未收到结束标记</exception>
    private static async Task<string> ReadUntilAsync(ChannelReader<string> reader, string marker, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var line))
            {
                if (line == marker)
                {
                    return builder.ToString();
                }

                builder.AppendLine(line);
            }
        }

        throw new IOException("ExifTool 进程已意外退出。");
    }

    /// <summary>
    /// 停止 ExifTool 进程并释放相关资源
    /// </summary>
    private void StopProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        _standardOutput = null;
        _standardError = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // 发送 stay_open 退出命令，告知 ExifTool 自行收尾
                process.StandardInput.WriteLine("-stay_open");
                process.StandardInput.WriteLine("False");
                process.StandardInput.Flush();

                if (!process.WaitForExit(ShutdownTimeout))
                {
                    process.Kill();
                }
            }
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 忽略强制结束时的错误
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            StopProcess();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
