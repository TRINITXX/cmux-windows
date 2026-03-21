using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cmux.Controls;

/// <summary>
/// WPF HwndHost that creates a child window for Direct3D 11 rendering.
/// The HWND is used as the target for the DXGI swap chain.
/// Raw Win32 mouse/keyboard messages are forwarded via the <see cref="RawInput"/> event
/// because HwndHost's airspace prevents normal WPF event routing.
/// </summary>
internal sealed class D3DRenderHost : HwndHost
{
    private const string ClassName = "CmuxD3DRenderHost";
    private static bool _classRegistered;
    private nint _hwnd;

    public nint Hwnd => _hwnd;

    /// <summary>
    /// Fired for every mouse/keyboard Win32 message received by the child HWND.
    /// Parameters: (int msg, nint wParam, nint lParam).
    /// </summary>
    public event Action<int, nint, nint>? RawInput;

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

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Suppress background erase — D3D11 swap chain handles all painting
        if (msg == 0x0014) // WM_ERASEBKGND
        {
            handled = true;
            return 1;
        }

        // Validate dirty region without GDI painting — swap chain covers the entire surface
        if (msg == 0x000F) // WM_PAINT
        {
            ValidateRect(hwnd, nint.Zero);
            handled = true;
            return nint.Zero;
        }

        if (IsInputMessage(msg) && RawInput != null)
        {
            RawInput.Invoke(msg, wParam, lParam);
            handled = true;
            return nint.Zero;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static bool IsInputMessage(int msg) => msg is
        (>= 0x0200 and <= 0x020E) or // WM_MOUSEMOVE through WM_MOUSEHWHEEL
        0x0100 or 0x0101 or 0x0102 or 0x0104 or 0x0105; // WM_KEY* and WM_CHAR

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
    private static extern bool ValidateRect(nint hwnd, nint lpRect);

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
