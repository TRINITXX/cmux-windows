using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Cmux.Controls;

/// <summary>
/// In-app browser control using WebView2. Provides a toolbar (back/forward/reload/address bar)
/// and a scriptable API for agents to interact with web pages.
/// DevTools are embedded via Win32 window reparenting of the native DevTools window.
/// </summary>
public partial class BrowserControl : UserControl
{
    public event Action? CloseRequested;

    private Task? _initTask;
    private string? _pendingUrl;
    private bool _devToolsVisible;
    private IntPtr _devToolsHwnd;
    private IntPtr _clipContainerHwnd;
    private int _titleBarHeight;
    private readonly System.Windows.Threading.DispatcherTimer _devToolsWatchdog = new()
        { Interval = TimeSpan.FromMilliseconds(500) };

    public BrowserControl()
    {
        InitializeComponent();
        InitializeWebView();
        LayoutUpdated += OnLayoutUpdated;
        _devToolsWatchdog.Tick += (_, _) =>
        {
            if (_devToolsVisible && (_devToolsHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_devToolsHwnd)))
            {
                _devToolsHwnd = IntPtr.Zero;
                HideDevTools();
            }
        };
    }

    private async void InitializeWebView()
    {
        _initTask = InitializeWebViewCore();
        await _initTask;
    }

    private async Task InitializeWebViewCore()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // Navigate to pending URL if any
            if (_pendingUrl != null)
            {
                WebView.CoreWebView2.Navigate(_pendingUrl);
                AddressBar.Text = _pendingUrl;
                _pendingUrl = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
        }
    }

    /// <summary>Navigate to a URL.</summary>
    public async void Navigate(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        AddressBar.Text = url;

        if (WebView.CoreWebView2 != null)
        {
            try { WebView.CoreWebView2.Navigate(url); } catch { }
        }
        else
        {
            _pendingUrl = url;
            if (_initTask != null)
            {
                await _initTask;
                if (_pendingUrl == url && WebView.CoreWebView2 != null)
                {
                    try { WebView.CoreWebView2.Navigate(url); } catch { }
                    _pendingUrl = null;
                }
            }
        }
    }

    /// <summary>Execute JavaScript and return the result.</summary>
    public async Task<string> EvaluateJavaScript(string script)
    {
        if (WebView.CoreWebView2 == null) return "";
        return await WebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>Get the accessibility tree snapshot (simplified).</summary>
    public async Task<string> GetAccessibilitySnapshot()
    {
        const string script = @"
            (function() {
                function walk(node) {
                    const result = {
                        role: node.getAttribute('role') || node.tagName.toLowerCase(),
                        name: node.getAttribute('aria-label') || node.textContent?.substring(0, 100) || '',
                        children: []
                    };
                    for (const child of node.children) {
                        result.children.push(walk(child));
                    }
                    return result;
                }
                return JSON.stringify(walk(document.body));
            })()
        ";
        return await EvaluateJavaScript(script);
    }

    /// <summary>Click an element by CSS selector.</summary>
    public async Task ClickElement(string selector)
    {
        var escapedSelector = selector.Replace("'", "\\'");
        await EvaluateJavaScript($"document.querySelector('{escapedSelector}')?.click()");
    }

    /// <summary>Fill a form field by CSS selector.</summary>
    public async Task FillElement(string selector, string value)
    {
        var escapedSelector = selector.Replace("'", "\\'");
        var escapedValue = value.Replace("'", "\\'");
        await EvaluateJavaScript($@"
            (() => {{
                const el = document.querySelector('{escapedSelector}');
                if (el) {{
                    el.value = '{escapedValue}';
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}
            }})()
        ");
    }

    /// <summary>Get the current page URL.</summary>
    public string GetCurrentUrl()
    {
        return WebView.CoreWebView2?.Source ?? "";
    }

    // ── DevTools (Win32 reparenting) ────────────────────────────────────

    private async void ToggleDevTools_Click(object sender, RoutedEventArgs e) => await ToggleDevTools();

    public async Task ToggleDevTools()
    {
        if (_devToolsVisible) { HideDevTools(); return; }
        if (WebView.CoreWebView2 == null) return;

        // If the DevTools window still exists, just show it again
        if (_devToolsHwnd != IntPtr.Zero && NativeMethods.IsWindow(_devToolsHwnd))
        {
            ShowDevToolsPane();
            if (_clipContainerHwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_clipContainerHwnd, NativeMethods.SW_SHOWNOACTIVATE);
            PositionDevToolsWindow();
            NativeMethods.ShowWindow(_devToolsHwnd, NativeMethods.SW_SHOWNOACTIVATE);
            return;
        }

        await OpenEmbeddedDevTools();
    }

    private async Task OpenEmbeddedDevTools()
    {
        // Snapshot all existing top-level windows so we can find the new one
        var existing = new HashSet<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) => { existing.Add(hwnd); return true; }, IntPtr.Zero);

        // Ask WebView2 to open DevTools (creates a native window)
        WebView.CoreWebView2!.OpenDevToolsWindow();

        // Poll for the new DevTools window
        IntPtr found = IntPtr.Zero;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(60);
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (existing.Contains(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
                    return true;
                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, sb, 256);
                if (sb.ToString().Contains("DevTools", StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) break;
        }

        if (found == IntPtr.Zero) return;

        // Hide immediately to minimize flash
        NativeMethods.ShowWindow(found, NativeMethods.SW_HIDE);
        _devToolsHwnd = found;

        // Measure title bar height before any modifications
        NativeMethods.GetWindowRect(_devToolsHwnd, out var wr);
        NativeMethods.GetClientRect(_devToolsHwnd, out var cr);
        _titleBarHeight = (wr.bottom - wr.top) - (cr.bottom - cr.top);
        if (_titleBarHeight < 5) _titleBarHeight = 31; // fallback

        // Get main window HWND
        var mainWindow = Window.GetWindow(this);
        if (mainWindow == null) return;
        var mainHwnd = new WindowInteropHelper(mainWindow).Handle;

        // Create a clip container — child windows are clipped to parent bounds,
        // so positioning DevTools at Y=-titleBarHeight hides the title bar
        if (_clipContainerHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_clipContainerHwnd))
        {
            _clipContainerHwnd = NativeMethods.CreateWindowEx(0, "static", "",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN,
                0, 0, 100, 100, mainHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        // Make DevTools a child of the clip container
        NativeMethods.SetWindowLong(_devToolsHwnd, NativeMethods.GWL_STYLE,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE);
        NativeMethods.SetParent(_devToolsHwnd, _clipContainerHwnd);

        // Show pane UI, then position & show
        ShowDevToolsPane();
        PositionDevToolsWindow();
        NativeMethods.ShowWindow(_devToolsHwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private void ShowDevToolsPane()
    {
        _devToolsVisible = true;
        DevToolsSplitterRow.Height = new GridLength(4);
        DevToolsRow.Height = new GridLength(1, GridUnitType.Star);
        DevToolsSplitter.Visibility = Visibility.Visible;
        DevToolsHostBorder.Visibility = Visibility.Visible;
        _devToolsWatchdog.Start();
    }

    private void HideDevTools()
    {
        _devToolsWatchdog.Stop();
        _devToolsVisible = false;
        DevToolsSplitterRow.Height = new GridLength(0);
        DevToolsRow.Height = new GridLength(0);
        DevToolsSplitter.Visibility = Visibility.Collapsed;
        DevToolsHostBorder.Visibility = Visibility.Collapsed;

        if (_devToolsHwnd != IntPtr.Zero && NativeMethods.IsWindow(_devToolsHwnd))
            NativeMethods.ShowWindow(_devToolsHwnd, NativeMethods.SW_HIDE);
        if (_clipContainerHwnd != IntPtr.Zero && NativeMethods.IsWindow(_clipContainerHwnd))
            NativeMethods.ShowWindow(_clipContainerHwnd, NativeMethods.SW_HIDE);
    }

    private void PositionDevToolsWindow()
    {
        if (_devToolsHwnd == IntPtr.Zero || !_devToolsVisible) return;
        if (!NativeMethods.IsWindow(_devToolsHwnd)) { _devToolsHwnd = IntPtr.Zero; return; }

        var mainWindow = Window.GetWindow(this);
        if (mainWindow == null) return;

        var source = PresentationSource.FromVisual(mainWindow);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var pos = DevToolsHostBorder.TransformToAncestor(mainWindow).Transform(new Point(0, 0));
        int w = (int)(DevToolsHostBorder.ActualWidth * dpiX);
        int h = (int)(DevToolsHostBorder.ActualHeight * dpiY);

        // Position clip container at DevToolsHostBorder's location
        if (_clipContainerHwnd != IntPtr.Zero && NativeMethods.IsWindow(_clipContainerHwnd))
            NativeMethods.MoveWindow(_clipContainerHwnd, (int)(pos.X * dpiX), (int)(pos.Y * dpiY), w, h, true);

        // Position DevTools inside container, shifted up to hide the title bar
        NativeMethods.MoveWindow(_devToolsHwnd, 0, -_titleBarHeight, w, h + _titleBarHeight, true);
    }

    // Reposition the DevTools native window whenever WPF layout changes
    private Point _lastPos;
    private Size _lastSize;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_devToolsHwnd == IntPtr.Zero || !_devToolsVisible) return;

        try
        {
            var mainWindow = Window.GetWindow(this);
            if (mainWindow == null) return;
            var pos = DevToolsHostBorder.TransformToAncestor(mainWindow).Transform(new Point(0, 0));
            var size = new Size(DevToolsHostBorder.ActualWidth, DevToolsHostBorder.ActualHeight);
            if (pos == _lastPos && size == _lastSize) return;
            _lastPos = pos;
            _lastSize = size;
            PositionDevToolsWindow();
        }
        catch { }
    }

    // ── Standard event handlers ─────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoBack == true) WebView.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2?.CanGoForward == true) WebView.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => WebView.CoreWebView2?.Reload();

    private void OpenInChrome_Click(object sender, RoutedEventArgs e)
    {
        var url = GetCurrentUrl();
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = url, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void CloseBrowser_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Navigate(AddressBar.Text); e.Handled = true; }
        else if (e.Key == Key.F12) { _ = ToggleDevTools(); e.Handled = true; }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => AddressBar.Text = WebView.CoreWebView2?.Source ?? "";

    private void WebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => AddressBar.Text = WebView.CoreWebView2?.Source ?? "";

    // ── Win32 interop ───────────────────────────────────────────────────

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_MAXIMIZEBOX = 0x00010000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_DLGMODALFRAME = 0x00000001;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint WM_CLOSE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }
    }
}
