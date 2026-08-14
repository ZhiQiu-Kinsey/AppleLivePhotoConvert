using System.Runtime.InteropServices;

namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// Windows 原生文件夹选择对话框（纯 Win32 P/Invoke，支持默认路径预选中，100% 兼容 Native AOT）
/// </summary>
[SupportedOSPlatform("windows")]
static partial class FolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint BffmInitialized = 1;
    private const uint BffmSetSelectionW = 0x0400 + 103; // WM_USER + 103

    private delegate int BrowseCallbackProc(IntPtr hwnd, uint uMsg, IntPtr lParam, IntPtr lpData);

    [StructLayout(LayoutKind.Sequential)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public IntPtr lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")]
    private static partial IntPtr SHBrowseForFolder(ref BrowseInfo bi);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SHGetPathFromIDList(IntPtr pidl, char* pszPath);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(IntPtr pvReserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    /// <summary>
    /// 弹出原生文件夹选择对话框
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <param name="initialDirectory">默认预选中的初始目录路径</param>
    /// <returns>选中的目录；用户取消或失败时返回 <c>null</c></returns>
    public static string? Show(string title, string? initialDirectory = null)
    {
        string? selected = null;
        var thread = new Thread(() =>
        {
            OleInitialize(IntPtr.Zero);
            var displayNameBuffer = Marshal.AllocHGlobal(520);
            IntPtr initialDirPtr = IntPtr.Zero;
            IntPtr titlePtr = IntPtr.Zero;
            BrowseCallbackProc? callback = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    initialDirPtr = Marshal.StringToHGlobalUni(initialDirectory);
                    callback = (hwnd, uMsg, _, lpData) =>
                    {
                        if (uMsg == BffmInitialized && lpData != IntPtr.Zero)
                        {
                            SendMessageW(hwnd, BffmSetSelectionW, new IntPtr(1), lpData);
                        }
                        return 0;
                    };
                }

                titlePtr = Marshal.StringToHGlobalUni(title);
                var bi = new BrowseInfo
                {
                    hwndOwner = GetForegroundWindow(),
                    pidlRoot = IntPtr.Zero,
                    pszDisplayName = displayNameBuffer,
                    lpszTitle = titlePtr,
                    ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle,
                    lpfn = callback is not null ? Marshal.GetFunctionPointerForDelegate(callback) : IntPtr.Zero,
                    lParam = initialDirPtr,
                    iImage = 0
                };

                var pidl = SHBrowseForFolder(ref bi);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        var pathBuffer = new char[1024];
                        unsafe
                        {
                            fixed (char* pBuffer = pathBuffer)
                            {
                                if (SHGetPathFromIDList(pidl, pBuffer))
                                {
                                    var raw = new string(pathBuffer);
                                    var end = raw.IndexOf('\0');
                                    selected = end >= 0 ? raw[..end] : raw;
                                }
                            }
                        }
                    }
                    finally
                    {
                        CoTaskMemFree(pidl);
                    }
                }
            }
            catch
            {
                selected = null;
            }
            finally
            {
                if (titlePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(titlePtr);
                }

                if (initialDirPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(initialDirPtr);
                }

                Marshal.FreeHGlobal(displayNameBuffer);
                OleUninitialize();
                GC.KeepAlive(callback);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return selected;
    }
}
