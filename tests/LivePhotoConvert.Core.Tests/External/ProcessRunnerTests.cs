using LivePhotoConvert.Core.External;

namespace LivePhotoConvert.Core.Tests.External;

/// <summary>用系统 shell 验证输出收集、超时与取消；Windows 用 cmd，其余系统用 sh。</summary>
public class ProcessRunnerTests
{
    /// <summary>取消或超时后应在此时间内返回；远大于正常结束进程所需的时间，只用来防止挂起。</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(20);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (string FileName, string[] Arguments) Shell(string unix, string windows) =>
        OperatingSystem.IsWindows() ? ("cmd.exe", ["/d", "/c", windows]) : ("/bin/sh", ["-c", unix]);

    private static (string FileName, string[] Arguments) LongRunning() =>
        Shell("sleep 60", "ping -n 60 127.0.0.1 >nul");

    [Fact]
    public async Task RunAsync_CollectsOutputsAndExitCode()
    {
        var (fileName, arguments) = Shell("echo out; echo first >&2; echo last >&2; exit 3", "echo out& echo first 1>&2& echo last 1>&2& exit /b 3");

        var result = await ProcessRunner.RunAsync(fileName, arguments, Token);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Success);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("last", result.ErrorSummary);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsProcessAndThrowsTimeout()
    {
        var (fileName, arguments) = LongRunning();

        await Assert.ThrowsAsync<TimeoutException>(
            () => ProcessRunner.RunAsync(fileName, arguments, Token, TimeSpan.FromMilliseconds(300)).WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task RunAsync_Canceled_KillsProcessAndThrowsCanceled()
    {
        var (fileName, arguments) = LongRunning();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.RunAsync(fileName, arguments, cts.Token).WaitAsync(HangGuard, Token));
    }

    [Fact]
    public async Task RunAsync_AlreadyCanceled_DoesNotStartProcess()
    {
        using var temp = new TempDirectory();
        var marker = temp.Combine("started");
        var (fileName, arguments) = Shell($"echo x > '{marker}'", $"echo x > \"{marker}\"");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(fileName, arguments, cts.Token));

        Assert.False(File.Exists(marker));
    }
}
