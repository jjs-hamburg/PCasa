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
    private const int SwShow = 5;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmLButtonDown = 0x0201;
    private const int WmKeyDown = 0x0100;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmSetCursor = 0x0020;

    private IntPtr _child;
    private bool _surfaceRevealed;

    // A fresh native STATIC window otherwise flashes its white GDI surface
    // while Direct3D is still building its first swap-chain frame. Keep it
    // hidden until the GPU has actually presented the configured backdrop.
    public void RevealRenderedSurface()
    {
        if (_surfaceRevealed || _child == IntPtr.Zero) return;
        _surfaceRevealed = true;
        ShowWindow(_child, SwShow);
    }

    internal bool IsSurfaceRevealed => _surfaceRevealed;

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
            WsChild | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1, hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_child == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Direct3D slideshow HWND could not be created.");
        _surfaceRevealed = false;
        return new HandleRef(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _child = IntPtr.Zero;
        _surfaceRevealed = false;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int message,
        IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSetCursor)
        {
            // WPF's Cursor=None does not cross the HwndHost airspace boundary.
            SetCursor(IntPtr.Zero);
            handled = true;
            return new IntPtr(1);
        }
        if (message == WmEraseBackground)
        {
            // Direct3D owns every pixel; never let STATIC erase to white.
            handled = true;
            return new IntPtr(1);
        }
        if (message == WmPaint)
        {
            // GDI painting of the native host would overwrite a valid GPU
            // frame with the class' default white STATIC background.
            ValidateRect(hwnd, IntPtr.Zero);
            handled = true;
            return IntPtr.Zero;
        }
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int showCommand);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

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
