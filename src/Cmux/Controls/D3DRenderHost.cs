using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Cmux.Controls;

/// <summary>
/// WPF HwndHost that creates a child window for Direct3D 11 rendering.
/// The HWND is used as the target for the DXGI swap chain.
/// Mouse/keyboard input is forwarded to WPF via routed events.
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
            WS_EX_TRANSPARENT, // Pass-through for mouse hit testing → events reach WPF
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
    private const int WS_EX_TRANSPARENT = 0x00000020;

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
