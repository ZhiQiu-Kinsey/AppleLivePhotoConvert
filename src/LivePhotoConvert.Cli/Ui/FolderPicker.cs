using System.Runtime.InteropServices;

namespace LivePhotoConvert.Cli.Ui;

/// <summary>
/// Windows 原生文件夹选择对话框（优先采用现代 IFileOpenDialog 对话框，自动降级至 SHBrowseForFolderW，100% 兼容 Native AOT）
/// </summary>
/// <remarks>
/// 传统 .NET 通过 <c>[ComImport]</c> 调用 COM 接口在 Native AOT 编译时存在裁剪和动态代理限制。
/// 本实现采用 C# 9+ 非托管函数指针 <c>delegate* unmanaged[Stdcall]&lt;...&gt;</c> 直接读取 COM 虚函数表（VTable），
/// 实现 100% 静态编译、零运行时反射、零动态代码生成的原生 Windows 文件夹对话框。
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class FolderPicker
{
    // 现代文件对话框 CLSID 与 IID
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidIFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    // COM 与 Shell 常量
    private const uint ClsctxInprocServer = 0x1;
    private const uint FosForceFileSystem = 0x40;      // 仅允许选择真实文件系统路径
    private const uint FosPathMustExist = 0x800;       // 路径必须已存在
    private const uint FosDontAddToRecent = 0x2000000; // 不加入最近使用记录
    private const uint SigdnFilesyspath = 0x80058000;  // 获取物理文件系统完整路径
    private const int HrErrorCancelled = unchecked((int)0x800704C7); // HRESULT 用户点击“取消”

    // 传统 SHBrowseForFolderW 常量
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifNewDialogStyle = 0x0040;
    private const uint BffmInitialized = 1;
    private const uint BffmSetSelectionW = 0x0400 + 103;

    /// <summary>
    /// 传统目录选择对话框回调函数委托
    /// </summary>
    private delegate int BrowseCallbackProc(IntPtr hwnd, uint uMsg, IntPtr lParam, IntPtr lpData);

    /// <summary>
    /// 传统 SHBrowseForFolder 对话框结构体
    /// </summary>
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

    [LibraryImport("ole32.dll")]
    private static partial void CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid,IntPtr pUnkOuter,uint dwClsContext,in Guid riid,out IntPtr ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string pszPath,IntPtr pbc,in Guid riid,out IntPtr ppv);

    [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")]
    private static partial IntPtr SHBrowseForFolder(ref BrowseInfo bi);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SHGetPathFromIDList(IntPtr pidl, char* pszPath);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial void SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial void SetProcessDPIAware();

    [LibraryImport("shcore.dll")]
    private static partial int SetProcessDpiAwareness(int awareness);

    /// <summary>
    /// 确保当前进程与对话框具备原生高分屏 PerMonitorV2 DPI 感知，彻底杜绝 2K/4K 屏幕下界面模糊与拉伸
    /// </summary>
    private static void EnsureHighDpiAware()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4) (Windows 10 1703+)
            if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
            {
                return;
            }
        }
        catch
        {
            // 忽略旧系统找不到 API 的情况
        }

        try
        {
            // PROCESS_PER_MONITOR_DPI_AWARE = 2 (Windows 8.1+)
            if (SetProcessDpiAwareness(2) == 0)
            {
                return;
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            // Windows Vista+ 系统级 DPI 感知兜底
            SetProcessDPIAware();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 在独立的 STA (Single-Threaded Apartment) 线程中弹出原生文件夹选择对话框
    /// </summary>
    /// <param name="title">对话框窗口标题</param>
    /// <param name="initialDirectory">默认预选中的初始目录路径（可为 null）</param>
    /// <returns>用户选中的目录完整路径；若用户取消或发生不可恢复异常则返回 <c>null</c></returns>
    public static string? Show(string title, string? initialDirectory = null)
    {
        string? selected = null;
        var thread = new Thread(() =>
        {
            // 开启原生高 DPI 感知
            EnsureHighDpiAware();

            // COINIT_APARTMENTTHREADED = 0x2 (COM 对话框要求必须在 STA 单元线程中运行)
            CoInitializeEx(IntPtr.Zero, 2);
            try
            {
                // 优先展示现代 IFileOpenDialog 对话框；若成功展示（包括用户主动点击取消），则不再降级重弹
                var (handled, path) = ShowModernDialog(title, initialDirectory);
                selected = handled ? path : ShowClassicBrowseFolder(title, initialDirectory);
            }
            finally
            {
                CoUninitialize();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return selected;
    }


    /// <summary>
    /// 弹出 Windows Vista+ 现代 IFileOpenDialog 对话框（同时展示目录内的所有图片/视频文件与缩略图，支持直接确认当前目录或选中任一文件）
    /// </summary>
    /// <remarks>
    /// IFileOpenDialog 虚函数表 VTable 索引布局：
    /// - [0..2] IUnknown: QueryInterface (0), AddRef (1), Release (2)
    /// - [3] IModalWindow: Show (3)
    /// - [4..26] IFileDialog: SetOptions (9), SetFolder (12), SetFileName (15), SetTitle (17), SetOkButtonLabel (18), SetFileNameLabel (19), GetResult (20) 等
    /// </remarks>
    private static unsafe (bool Handled, string? SelectedPath) ShowModernDialog(string title, string? initialDirectory)
    {
        IntPtr pDialog = IntPtr.Zero;
        IntPtr pInitialFolder = IntPtr.Zero;
        IntPtr pResultItem = IntPtr.Zero;
        char* pszPath = null;

        try
        {
            // 1. 创建 IFileOpenDialog COM 实例
            var hr = CoCreateInstance(ClsidFileOpenDialog, IntPtr.Zero, ClsctxInprocServer, IidIFileOpenDialog, out pDialog);
            if (hr != 0 || pDialog == IntPtr.Zero)
            {
                // 创建 COM 实例失败，标记为未处理并允许降级
                return (false, null);
            }

            // 2. SetOptions (VTable 索引 9)：强制文件系统路径、路径必须存在，不设 FOS_PICKFOLDERS 从而展示文件夹内所有真实文件
            var setOptions = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)(*(void***)pDialog)[9];
            setOptions(pDialog, FosForceFileSystem | FosPathMustExist | FosDontAddToRecent);

            // 3. SetTitle (VTable 索引 17)：设置对话框标题提示
            var fullTitle = $"{title}（进入目录点击【选择此文件夹】，或选中目录内任一文件）";
            fixed (char* pTitle = fullTitle)
            {
                var setTitle = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)(*(void***)pDialog)[17];
                setTitle(pDialog, pTitle);
            }

            // 4. SetOkButtonLabel (VTable 索引 18)：将确定按钮标签改为“选择此文件夹”
            fixed (char* pOkLabel = "选择此文件夹")
            {
                var setOkLabel = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)(*(void***)pDialog)[18];
                setOkLabel(pDialog, pOkLabel);
            }

            // 5. SetFileName (VTable 索引 15)：默认填入占位名，使用户直接点击“选择此文件夹”即可选定当前所在目录
            fixed (char* pFileName = "[选择当前目录]")
            {
                var setFileName = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)(*(void***)pDialog)[15];
                setFileName(pDialog, pFileName);
            }

            // 6. SetFolder (VTable 索引 12)：设置默认初始定位目录
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                if (SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, IidIShellItem, out pInitialFolder) == 0 && pInitialFolder != IntPtr.Zero)
                {
                    var setFolder = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)pDialog)[12];
                    setFolder(pDialog, pInitialFolder);
                }
            }

            // 7. Show (VTable 索引 3)：以控制台前台窗口为父窗口模态弹出
            var show = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)pDialog)[3];
            var showResult = show(pDialog, GetForegroundWindow());
            if (showResult == HrErrorCancelled)
            {
                // 用户主动点击“取消”或关闭窗口，标记已处理并返回 null，绝不二次弹出传统对话框
                return (true, null);
            }
            if (showResult != 0)
            {
                // 对话框展示异常，允许降级
                return (false, null);
            }

            // 8. GetResult (VTable 索引 20)：获取选中的 IShellItem 接口指针
            var getResult = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)pDialog)[20];
            if (getResult(pDialog, &pResultItem) != 0 || pResultItem == IntPtr.Zero)
            {
                return (true, null);
            }

            // 9. IShellItem.GetDisplayName (VTable 索引 5)：提取选中的物理绝对路径
            var getDisplayName = (delegate* unmanaged[Stdcall]<IntPtr, uint, char**, int>)(*(void***)pResultItem)[5];
            if (getDisplayName(pResultItem, SigdnFilesyspath, &pszPath) == 0 && pszPath != null)
            {
                var rawPath = new string(pszPath);

                // 若选中的本身就是文件夹
                if (Directory.Exists(rawPath))
                {
                    return (true, rawPath);
                }

                // 若选中了文件夹内的某个文件（或占位虚拟名），取其所在目录
                if (File.Exists(rawPath))
                {
                    var parentDir = Path.GetDirectoryName(rawPath);
                    return (true, string.IsNullOrEmpty(parentDir) ? rawPath : parentDir);
                }

                // 处理未选具体文件、直接点击“选择此文件夹”返回的占位路径
                var parent = Path.GetDirectoryName(rawPath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    return (true, parent);
                }

                return (true, rawPath);
            }

            return (true, null);
        }
        catch
        {
            return (false, null);
        }
        finally
        {
            // 严密释放非托管 COM 内存与指针引用
            if (pszPath != null)
            {
                CoTaskMemFree((IntPtr)pszPath);
            }

            if (pResultItem != IntPtr.Zero)
            {
                var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)pResultItem)[2];
                release(pResultItem);
            }

            if (pInitialFolder != IntPtr.Zero)
            {
                var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)pInitialFolder)[2];
                release(pInitialFolder);
            }

            if (pDialog != IntPtr.Zero)
            {
                var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)pDialog)[2];
                release(pDialog);
            }
        }
    }



    /// <summary>
    /// 传统 SHBrowseForFolder 树形文件夹选择器（作为旧版系统或 COM 故障时的回退保障）
    /// </summary>
    private static string? ShowClassicBrowseFolder(string title, string? initialDirectory)
    {
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
                                return end >= 0 ? raw[..end] : raw;
                            }
                        }
                    }
                }
                finally
                {
                    CoTaskMemFree(pidl);
                }
            }

            return null;
        }
        catch
        {
            return null;
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
            GC.KeepAlive(callback);
        }
    }
}


