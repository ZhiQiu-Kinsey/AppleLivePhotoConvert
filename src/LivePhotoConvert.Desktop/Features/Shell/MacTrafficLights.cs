using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace LivePhotoConvert.Desktop.Features.Shell;

/// <summary>
/// 把 macOS 交通灯垂直居中到自绘标题栏里。
/// 系统标题栏比这里的标题栏矮，按钮会贴在顶部；原生布局之后再改按钮坐标。
/// </summary>
internal static class MacTrafficLights
{
    public static void CenterIn(Window window, double titleBarHeight)
    {
        if (!OperatingSystem.IsMacOS() || titleBarHeight <= 0)
        {
            return;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is not { Handle: var native } || native == IntPtr.Zero)
        {
            return;
        }

        if (handle.HandleDescriptor is not ("NSView" or "NSWindow"))
        {
            return;
        }

        try
        {
            var nsWindow = handle.HandleDescriptor == "NSWindow" ? native : ObjC.Send(native, "window");
            if (nsWindow == IntPtr.Zero)
            {
                return;
            }

            var content = ObjC.Send(nsWindow, "contentView");
            if (content == IntPtr.Zero)
            {
                return;
            }

            var bounds = ObjC.Frame(content, "bounds");
            var bottom = ObjC.Convert(content, 0, 0);
            var top = ObjC.Convert(content, 0, bounds.Height);
            var targetCenterY = Math.Max(bottom.Y, top.Y) - titleBarHeight / 2;

            for (nint buttonId = 0; buttonId < 3; buttonId++)
            {
                var button = ObjC.Send(nsWindow, "standardWindowButton:", buttonId);
                if (button == IntPtr.Zero)
                {
                    continue;
                }

                ShiftVertically(button, targetCenterY);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>沿窗口的垂直方向平移按钮，父视图是否翻转都按同一屏幕方向移动。</summary>
    private static void ShiftVertically(IntPtr button, double targetCenterY)
    {
        var frame = ObjC.Frame(button, "frame");
        var superview = ObjC.Send(button, "superview");
        if (superview == IntPtr.Zero || frame.Height <= 0)
        {
            return;
        }

        var center = ObjC.Convert(superview, frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        var delta = targetCenterY - center.Y;
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        var origin = ObjC.Convert(superview, frame.X, frame.Y);
        var stepped = ObjC.Convert(superview, frame.X, frame.Y + 1);
        var scale = stepped.Y - origin.Y;
        if (Math.Abs(scale) < 0.1)
        {
            return;
        }

        frame.Y += delta / scale;
        ObjC.SetFrame(button, frame);
    }

    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        public static IntPtr Send(IntPtr receiver, string selector) =>
            SendId(receiver, sel_registerName(selector));

        public static IntPtr Send(IntPtr receiver, string selector, nint arg) =>
            SendNInt(receiver, sel_registerName(selector), arg);

        public static CGRect Frame(IntPtr receiver, string selector)
        {
            var sel = sel_registerName(selector);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                SendRectStret(out var rect, receiver, sel);
                return rect;
            }

            return SendRect(receiver, sel);
        }

        public static CGPoint Convert(IntPtr view, double x, double y) =>
            SendPoint(view, sel_registerName("convertPoint:toView:"), new CGPoint(x, y), IntPtr.Zero);

        public static void SetFrame(IntPtr view, CGRect frame) =>
            SendVoidRect(view, sel_registerName("setFrame:"), frame);

        [DllImport(Lib)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendId(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendNInt(IntPtr receiver, IntPtr selector, nint arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend_stret")]
        private static extern void SendRectStret(out CGRect rect, IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector, CGPoint point, IntPtr view);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidRect(IntPtr receiver, IntPtr selector, CGRect frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;

        public CGPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }
}
