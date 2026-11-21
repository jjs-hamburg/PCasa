using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PCasa.App.Services;

/// <summary>
/// Native child HWND for the Direct3D slideshow. HwndHost avoids enabling
/// Windows Forms for the entire WPF application (and the resulting global
/// System.Windows.Forms / System.Drawing type-name collisions).
/// </summary>
internal sealed class Direct3DSlideshowHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmLButtonDown = 0x0201;
    private const int WmKeyDown = 0x0100;

    private IntPtr _child;

    public event Action? Clicked;
    public event Action<int>? NativeKeyDown;

    public IntPtr WindowHandle => _child;

    public (int Width, int Height) ClientSize
    {
        get
        {
            if (_child != IntPtr.Zero &&
                GetClientRect(_child, out var bounds))
                return (Math.Max(1, bounds.Right - bounds.Left),
                    Math.Max(1, bounds.Bottom - bounds.Top));
            return (1, 1);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _child = CreateWindowExW(0, "STATIC", "",
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1, hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_child == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Direct3D slideshow HWND could not be created.");
        return new HandleRef(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _child = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int message,
        IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmLButtonDown)
        {
            handled = true;
            Clicked?.Invoke();
            return IntPtr.Zero;
        }
        if (message == WmKeyDown)
        {
            handled = true;
            NativeKeyDown?.Invoke(wParam.ToInt32());
            return IntPtr.Zero;
        }
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent,
        IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rectangle);
}
