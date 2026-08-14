using LivePhotoConvert.Core.Services;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 错误日志静默记录器 (ErrorLogger) 的单元测试
/// </summary>
public class ErrorLoggerTests
{
    /// <summary>
    /// 测试 Log 是否能将异常类型、异常消息、内部异常和上下文说明正确格式化并追加写入到本地日志文件
    /// </summary>
    [Fact]
    public void Log_Should_Write_Exception_Details_To_Log_File()
    {
        var ex = new InvalidOperationException("测试异常消息", new ArgumentException("内部参数错误"));
        var logPath = ErrorLogger.Log(ex, "单元测试上下文");

        Assert.True(File.Exists(logPath));
        var content = File.ReadAllText(logPath);
        Assert.Contains("测试异常消息", content);
        Assert.Contains("内部参数错误", content);
        Assert.Contains("单元测试上下文", content);
    }
}

