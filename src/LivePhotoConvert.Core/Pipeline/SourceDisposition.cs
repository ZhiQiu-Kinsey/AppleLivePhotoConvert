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
/// <param name="action">处理方式</param>
/// <param name="archiveFolderName"><see cref="SourceFileAction.Move"/> 时的子文件夹名</param>
public sealed class SourceDisposition(SourceFileAction action, string archiveFolderName)
{
    public const string MergedFolderName = "已合成";
    public const string SplitFolderName = "已拆分";

    private readonly Lock _moveGate = new();

    public SourceFileAction Action => action;

    /// <summary>
    /// 处理一组源文件；返回失败说明，全部成功时返回 <c>null</c>。
    /// </summary>
    public string? Apply(params IReadOnlyList<string> paths)
    {
        if (action == SourceFileAction.Keep)
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
                (errors ??= []).Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }

        return errors is null ? null : string.Join("；", errors);
    }

    private void ApplyOne(string path)
    {
        switch (action)
        {
            case SourceFileAction.Move:
                var archive = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, archiveFolderName);
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
