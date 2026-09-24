using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace LivePhotoConvert.Core.Metadata;

/// <summary>
/// 一条 ExifTool 命令的输出。
/// </summary>
internal sealed record ExifToolResponse(string StandardOutput, string StandardError)
{
    /// <summary>
    /// 标准错误中除 Warning 以外的内容视为失败。
    /// </summary>
    public bool HasErrors
    {
        get
        {
            foreach (var line in StandardError.AsSpan().EnumerateLines())
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty && !trimmed.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int UpdatedFileCount
    {
        get
        {
            foreach (var line in StandardOutput.AsSpan().EnumerateLines())
            {
                var trimmed = line.Trim();
                var index = trimmed.IndexOf(" image files updated", StringComparison.Ordinal);
                if (index > 0 && int.TryParse(trimmed[..index], out var count))
                {
                    return count;
                }
            }

            return 0;
        }
    }

    public override string ToString() => (StandardOutput.Trim(), StandardError.Trim()) switch
    {
        ("", "") => "无输出",
        (var output, "") => output,
        ("", var error) => error,
        var (output, error) => $"{output}（{error}）"
    };
}

/// <summary>
/// 以 -stay_open 模式常驻的 ExifTool 进程；一次只执行一条命令。
/// </summary>
/// <remarks>
/// ExifTool 是打包的 Perl 程序，每次启动需要数百毫秒，常驻进程可以把批量处理的开销降到每条命令几毫秒。
/// 参数通过标准输入逐行传递，因此含换行符的参数会被拆成多个参数，必须拒绝。
/// </remarks>
internal sealed class ExifToolSession(string executablePath, string configPath) : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Channel<string>? _standardOutput;
    private Channel<string>? _standardError;
    private int _sequence;
    private bool _disposed;

    public async Task<ExifToolResponse> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        foreach (var argument in arguments)
        {
            if (argument.AsSpan().ContainsAny('\r', '\n'))
            {
                throw new ArgumentException($"参数包含换行符，无法安全传递给 ExifTool：{argument}", nameof(arguments));
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureStarted();
            var sequence = ++_sequence;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                await WriteCommandAsync(arguments, sequence, timeout.Token);
                var output = await ReadUntilAsync(_standardOutput!.Reader, $"{{ready{sequence}}}", timeout.Token);
                var error = await ReadUntilAsync(_standardError!.Reader, $"{{readyerr{sequence}}}", timeout.Token);
                return new ExifToolResponse(output, error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopProcess();
                throw new TimeoutException($"ExifTool 在 {CommandTimeout.TotalSeconds} 秒内没有响应。");
            }
            catch
            {
                // 命令中断后管道状态未知，下一条命令重新启动进程
                StopProcess();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        StopProcess();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 标准输入不能带 BOM，否则会粘在第一条命令的第一个参数前
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8
        };

        // -config 必须是第一个参数；-common_args 之后的参数作用于每条命令
        foreach (var argument in (ReadOnlySpan<string>)
                 ["-config", configPath, "-stay_open", "True", "-@", "-",
                  "-common_args", "-charset", "filename=UTF8", "-api", "QuickTimeUTC=1", "-api", "LargeFileSupport=1"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ExifTool。");
        _standardOutput = Pump(process.StandardOutput);
        _standardError = Pump(process.StandardError);
        _process = process;
    }

    private async Task WriteCommandAsync(IReadOnlyList<string> arguments, int sequence, CancellationToken cancellationToken)
    {
        var input = _process!.StandardInput;
        foreach (var argument in arguments)
        {
            await input.WriteLineAsync(argument.AsMemory(), cancellationToken);
        }

        // ExifTool 只在标准输出打印结束标记；-echo4 让标准错误也输出标记，才能判断错误输出何时结束
        await input.WriteLineAsync("-echo4".AsMemory(), cancellationToken);
        await input.WriteLineAsync($"{{readyerr{sequence}}}".AsMemory(), cancellationToken);
        await input.WriteLineAsync($"-execute{sequence}".AsMemory(), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private static Channel<string> Pump(StreamReader reader)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    channel.Writer.TryWrite(line);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex is ObjectDisposedException ? null : ex);
            }
        });
        return channel;
    }

    private static async Task<string> ReadUntilAsync(ChannelReader<string> reader, string marker, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var line in reader.ReadAllAsync(cancellationToken))
        {
            if (line == marker)
            {
                return builder.ToString();
            }

            builder.AppendLine(line);
        }

        throw new IOException("ExifTool 进程意外退出。");
    }

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
                process.StandardInput.WriteLine("-stay_open");
                process.StandardInput.WriteLine("False");
                process.StandardInput.Flush();
                if (!process.WaitForExit(ShutdownTimeout))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程已退出或无权结束，放弃即可
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
    }
}
