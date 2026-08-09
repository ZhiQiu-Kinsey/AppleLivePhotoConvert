using System.Runtime.InteropServices;

namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// Windows 原生文件夹选择对话框（纯 Win32 P/Invoke，支持默认路径预选中，100% 兼容 Native AOT）
/// </summary>
[SupportedOSPlatform("windows")]
static class FolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint BffmInitialized = 1;
    private const uint BffmSetSelectionW = 0x0400 + 103; // WM_USER + 103

    private delegate int BrowseCallbackProc(IntPtr hwnd, uint uMsg, IntPtr lParam, IntPtr lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", EntryPoint = "SHBrowseForFolderW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo bi);

    [DllImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, [Out] char[] pszPath);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

                var bi = new BrowseInfo
                {
                    hwndOwner = GetForegroundWindow(),
                    pidlRoot = IntPtr.Zero,
                    pszDisplayName = displayNameBuffer,
                    lpszTitle = title,
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
                        if (SHGetPathFromIDList(pidl, pathBuffer))
                        {
                            var raw = new string(pathBuffer);
                            var end = raw.IndexOf('\0');
                            selected = end >= 0 ? raw[..end] : raw;
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
