using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LivePhotoConvert.Core.Platform;

/// <summary>
/// Windows 回收站操作
/// </summary>
/// <remarks>项目开启了 PublishAot，因此使用 DllImport 而不是 Microsoft.VisualBasic.FileIO。</remarks>
public static class RecycleBin
{
    /// <summary>
    /// 删除文件（放入回收站）
    /// </summary>
    private const uint FoDelete = 0x0003;

    /// <summary>
    /// 允许撤销，即放入回收站而不是直接删除
    /// </summary>
    private const ushort FofAllowUndo = 0x0040;

    /// <summary>
    /// 不显示确认对话框
    /// </summary>
    private const ushort FofNoConfirmation = 0x0010;

    /// <summary>
    /// 不显示进度对话框
    /// </summary>
    private const ushort FofSilent = 0x0004;

    /// <summary>
    /// 出错时不弹出界面
    /// </summary>
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>
    /// SHFileOperation 的参数结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    /// <summary>
    /// 调用 Windows Shell 执行文件操作
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>
    /// 将文件删除到回收站
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <exception cref="IOException">操作失败或文件仍然存在</exception>
    [SupportedOSPlatform("windows")]
    public static void Send(string filePath)
    {
        // SHFileOperation 需要绝对路径，且 pFrom 是以 \0 分隔、再以 \0 结尾的列表
        var fullPath = Path.GetFullPath(filePath);
        var fileOp = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = fullPath + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
        };

        var result = SHFileOperation(ref fileOp);
        if (result != 0)
        {
            throw new IOException($"移入回收站失败，SHFileOperation 返回 0x{result:X8}。");
        }

        if (fileOp.fAnyOperationsAborted != 0)
        {
            throw new IOException("移入回收站的操作被中止。");
        }

        // 兜底校验，避免 Shell 返回成功但文件并未被移走
        if (File.Exists(fullPath))
        {
            throw new IOException("移入回收站后文件仍然存在。");
        }
    }
}
