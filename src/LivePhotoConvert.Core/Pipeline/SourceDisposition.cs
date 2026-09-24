using LivePhotoConvert.Core.Io;
using LivePhotoConvert.Core.Platform;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 转换成功后对源文件的处理方式。
/// </summary>
public enum SourceFileAction
{
    Keep = 0,

    /// <summary>移到源文件所在目录下的归档子文件夹。</summary>
    Move = 1,

    /// <summary>移入回收站（仅 Windows）。</summary>
    Recycle = 2,

    /// <summary>永久删除。</summary>
    Delete = 3
}

/// <summary>
/// 按所选方式处理已成功转换的源文件。只在输出落盘并校验通过后调用。
/// </summary>
public sealed class SourceDisposition
{
    private readonly SourceFileAction _action;
    private readonly string _archiveFolderName;
    private readonly Lock _moveGate = new();

    /// <param name="action">处理方式</param>
    /// <param name="archiveFolderName"><see cref="SourceFileAction.Move"/> 时的子文件夹名（单级目录名，由调用方按界面语言提供）；其它方式忽略</param>
    /// <exception cref="ArgumentException"><see cref="SourceFileAction.Move"/> 时名称为空或不是合法的单级目录名</exception>
    public SourceDisposition(SourceFileAction action, string? archiveFolderName = null)
    {
        if (action == SourceFileAction.Move && !IsValidFolderName(archiveFolderName))
        {
            throw new ArgumentException($"归档子文件夹名无效：\"{archiveFolderName}\"。", nameof(archiveFolderName));
        }

        _action = action;
        _archiveFolderName = archiveFolderName ?? string.Empty;
    }

    public SourceFileAction Action => _action;

    /// <summary>
    /// 处理一组源文件；返回失败的技术细节（文件名与异常原文），全部成功时返回 <c>null</c>。
    /// </summary>
    public string? Apply(params IReadOnlyList<string> paths)
    {
        if (_action == SourceFileAction.Keep)
        {
            return null;
        }

        List<string>? errors = null;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    ApplyOne(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                (errors ??= []).Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return errors is null ? null : string.Join("; ", errors);
    }

    /// <summary>名称来自本地化资源，写错时宁可拒绝，也不能把源文件移出所在目录。</summary>
    private static bool IsValidFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == name.Trim()
        && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private void ApplyOne(string path)
    {
        switch (_action)
        {
            case SourceFileAction.Move:
                var archive = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, _archiveFolderName);
                Directory.CreateDirectory(archive);
                // 解析文件名与移动必须在同一临界区，否则并发任务可能选中同一目标
                lock (_moveGate)
                {
                    File.Move(path, UniquePath.Resolve(archive, Path.GetFileName(path)));
                }

                break;
            case SourceFileAction.Recycle:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("当前系统不支持回收站。");
                }

                RecycleBin.Send(path);
                break;
            case SourceFileAction.Delete:
                File.Delete(path);
                break;
        }
    }
}
