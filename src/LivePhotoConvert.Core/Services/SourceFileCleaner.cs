using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Models;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Services;

/// <summary>
/// 一次清理的结果
/// </summary>
/// <param name="CleanedCount">成功处理的文件数量</param>
/// <param name="Failures">处理失败的文件</param>
public sealed record CleanupResult(int CleanedCount, IReadOnlyList<FailureRecord> Failures)
{
    /// <summary>
    /// 什么都没做的结果
    /// </summary>
    public static CleanupResult Empty { get; } = new(0, []);
}

/// <summary>
/// 按指定方式清理已合成成功的原始文件
/// </summary>
/// <remarks>
/// 调用方必须保证传入的文件已匹配成功且合成校验通过；未匹配的文件绝不应该进入这里。
/// 单个文件处理失败只会被记录，不会中断流程，已合成好的照片不受影响。
/// </remarks>
public sealed class SourceFileCleaner
{
    /// <summary>
    /// 移动模式下，存放已合成原始文件的子文件夹名称
    /// </summary>
    public const string MergedFolderName = "已合成";

    private readonly SourceFileAction _action;
    private readonly string? _mergedDirectory;
    private readonly string? _initializationError;

    /// <summary>
    /// 移动模式下「解析可用文件名 + 移动」必须是原子操作，否则并行时两个线程可能选中同一个目标路径
    /// </summary>
    private readonly Lock _moveGate = new();

    /// <summary>
    /// 创建清理器
    /// </summary>
    /// <param name="action">处理方式</param>
    /// <param name="inputDirectory">输入目录，移动模式下子文件夹建在此目录下</param>
    public SourceFileCleaner(SourceFileAction action, string inputDirectory)
    {
        _action = action;
        if (action != SourceFileAction.Move)
        {
            return;
        }

        _mergedDirectory = Path.Combine(inputDirectory, MergedFolderName);
        try
        {
            Directory.CreateDirectory(_mergedDirectory);
        }
        catch (Exception ex)
        {
            // 子文件夹建不出来时不向外抛，避免把清理问题误报成合成失败
            _initializationError = $"无法创建 \"{MergedFolderName}\" 子文件夹，{ex.Message}";
        }
    }

    /// <summary>
    /// 清理一组已合成成功的原始文件
    /// </summary>
    /// <param name="filePaths">需要清理的原始文件路径</param>
    /// <returns>清理结果</returns>
    public CleanupResult Clean(IReadOnlyList<string> filePaths)
    {
        if (_action == SourceFileAction.Keep)
        {
            return CleanupResult.Empty;
        }

        if (_initializationError is not null)
        {
            return new CleanupResult(0, [.. filePaths.Select(path => new FailureRecord(path, _initializationError))]);
        }

        var cleaned = 0;
        List<FailureRecord>? failures = null;
        foreach (var filePath in filePaths)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                CleanOne(filePath);
                cleaned++;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(new FailureRecord(filePath, ex.Message));
            }
        }

        return new CleanupResult(cleaned, failures ?? (IReadOnlyList<FailureRecord>)[]);
    }

    /// <summary>
    /// 按所选方式处理单个文件
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <exception cref="PlatformNotSupportedException">在非 Windows 系统上请求回收站</exception>
    private void CleanOne(string filePath)
    {
        switch (_action)
        {
            case SourceFileAction.Move:
                // 重名时追加 _1、_2 后缀，绝不覆盖已有文件
                lock (_moveGate)
                {
                    File.Move(filePath, UniquePath.Resolve(_mergedDirectory!, Path.GetFileName(filePath)));
                }

                break;
            case SourceFileAction.Recycle:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("当前系统不支持回收站。");
                }
                RecycleBin.Send(filePath);
                break;
            case SourceFileAction.Delete:
                File.Delete(filePath);
                break;
            case SourceFileAction.Keep:
            default:
                break;
        }
    }
}
