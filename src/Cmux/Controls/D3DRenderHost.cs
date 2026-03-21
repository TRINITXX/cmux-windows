using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Cmux.Controls;

/// <summary>
/// WPF HwndHost that creates a child window for Direct3D 11 rendering.
/// The HWND is used as the target for the DXGI swap chain.
/// Mouse/keyboard Win32 messages are translated into WPF routed events
/// so the parent TerminalControl receives them normally.
/// </summary>
internal sealed class D3DRenderHost : HwndHost
{
    private const string ClassName = "CmuxD3DRenderHost";
    private static bool _classRegistered;
    private nint _hwnd;

    public nint Hwnd => _hwnd;

    public D3DRenderHost()
    {
        Focusable = true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();

        _hwnd = CreateWindowEx(
            0,
            ClassName,
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0,
            (int)ActualWidth, (int)ActualHeight,
            hwndParent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = nint.Zero;
    }

    /// <summary>
    /// Intercept Win32 messages on the child HWND and re-raise them as WPF
    /// routed events so they bubble up to TerminalControl.
    /// </summary>
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEWHEEL:
            {
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }

            case WM_LBUTTONDOWN:
            {
                // Give WPF focus to this element first (fixes first-click-ignored issue)
                Focus();
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }

            case WM_LBUTTONUP:
            {
                ReleaseMouseCapture();
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }

            case WM_RBUTTONDOWN:
            {
                Focus();
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonDownEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }

            case WM_RBUTTONUP:
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonUpEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }

            case WM_MOUSEMOVE:
            {
                var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = UIElement.MouseMoveEvent,
                };
                RaiseEvent(args);
                handled = true;
                return nint.Zero;
            }
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = DefWindowProcPtr,
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };

        RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    // Win32 constants
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;

    private static readonly nint DefWindowProcPtr =
        GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }
}
