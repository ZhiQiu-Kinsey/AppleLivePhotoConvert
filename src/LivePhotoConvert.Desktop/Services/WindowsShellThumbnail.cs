using System.Runtime.InteropServices;
using ImageMagick;
namespace LivePhotoConvert.Desktop.Services;

public static class WindowsShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        in Guid riid,
        out IntPtr ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[]? lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public readonly record struct ShellThumbResult(byte[] JpgBytes, int Width, int Height);

    /// <summary>
    /// 调用 Windows Shell 硬件加速提取缩略图，避免解码完整原始点阵。
    /// </summary>
    public static unsafe ShellThumbResult? TryExtractThumbnail(string filePath, int targetSize)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        bool coInit = false;
        int initHr = CoInitializeEx(IntPtr.Zero, 0x2 | 0x4); // COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE
        if (initHr == 0 || initHr == 1)
        {
            coInit = true;
        }

        IntPtr pFactory = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, in ShellItemImageFactoryGuid, out pFactory);
            if (hr != 0 || pFactory == IntPtr.Zero)
            {
                return null;
            }

            // IShellItemImageFactory vtable:
            // [0] QueryInterface, [1] AddRef, [2] Release, [3] GetImage
            IntPtr* vtable = *(IntPtr**)pFactory;
            var getImage = (delegate* unmanaged[Stdcall]<IntPtr, SIZE, SIIGBF, out IntPtr, int>)vtable[3];

            int hrImg = getImage(pFactory, new SIZE(targetSize, targetSize), SIIGBF.SIIGBF_RESIZETOFIT, out IntPtr hBitmap);
            if (hrImg != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                BITMAPINFOHEADER bih = new()
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>()
                };

                GetDIBits(hdc, hBitmap, 0, 0, null, ref bih, 0);
                int width = bih.biWidth;
                int height = Math.Abs(bih.biHeight);
                if (width <= 0 || height <= 0)
                {
                    return null;
                }

                bih.biHeight = -height;
                bih.biCompression = 0;
                int bufferSize = width * height * 4;
                byte[] pixels = new byte[bufferSize];

                int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bih, 0);
                if (lines == 0)
                {
                    return null;
                }

                var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA);
                using var img = new MagickImage(pixels, settings);
                img.Format = MagickFormat.Jpeg;
                img.Quality = 92;
                return new ShellThumbResult(img.ToByteArray(), width, height);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pFactory != IntPtr.Zero)
            {
                IntPtr* vtable = *(IntPtr**)pFactory;
                var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
                release(pFactory);
            }

            if (coInit)
            {
                CoUninitialize();
            }
        }
    }
}
