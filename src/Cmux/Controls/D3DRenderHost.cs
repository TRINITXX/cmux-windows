using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cmux.Controls;

/// <summary>
/// WPF HwndHost that creates a child window for Direct3D 11 rendering.
/// The HWND is used as the target for the DXGI swap chain.
/// </summary>
internal sealed class D3DRenderHost : HwndHost
{
    private const string ClassName = "CmuxD3DRenderHost";
    private static bool _classRegistered;
    private nint _hwnd;

    public nint Hwnd => _hwnd;

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
    /// Forward mouse and keyboard messages from the child HWND to the WPF parent
    /// so that TerminalControl receives OnMouseWheel, OnMouseDown, OnKeyDown, etc.
    /// </summary>
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Forward all mouse and keyboard messages to the parent HWND
        if (IsInputMessage(msg))
        {
            var parent = GetParent(hwnd);
            if (parent != nint.Zero)
            {
                SendMessage(parent, msg, wParam, lParam);
                handled = true;
                return nint.Zero;
            }
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static bool IsInputMessage(int msg) => msg switch
    {
        WM_MOUSEWHEEL or WM_MOUSEHWHEEL => true,
        >= WM_MOUSEFIRST and <= WM_MOUSELAST => true,
        WM_KEYDOWN or WM_KEYUP or WM_CHAR or WM_SYSKEYDOWN or WM_SYSKEYUP => true,
        _ => false,
    };

    private const int WM_MOUSEFIRST = 0x0200;
    private const int WM_MOUSELAST = 0x020D;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_CHAR = 0x0102;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

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

    // Win32 interop
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    private static readonly nint DefWindowProcPtr =
        GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

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
