using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cmux.Core.Config;
using Cmux.Core.Models;
using Cmux.Core.Terminal;
using Cmux.Rendering;

namespace Cmux.Controls;

/// <summary>
/// WPF control that renders a TerminalBuffer and handles keyboard/mouse input.
/// Uses D3D11 GPU rendering via D3DTerminalRenderer for the terminal cell grid.
/// Features: scrollback, URL detection, search highlights, mouse reporting, visual bell.
/// </summary>
public class TerminalControl : FrameworkElement
{
    private TerminalSession? _session;
    private readonly TerminalSelection _selection = new();
    private GhosttyTheme _theme;
    private Typeface _typeface;

    // GPU renderer
    private D3DRenderHost? _renderHost;
    private D3DTerminalRenderer? _gpuRenderer;
    private bool _gpuInitialized;

    private Point? _rawMousePosition; // Mouse position from Win32 lParam (WPF DIPs)
    private double _cellWidth;
    private double _cellHeight;
    private double _fontSize;
    private int _cols;
    private int _rows;
    private bool _mouseDown;
    private int _scrollOffset; // Negative = scrolled into history, 0 = at bottom
    private int _scrollWheelRemainder; // Fractional wheel accumulator (WezTerm-style)
    private const double HorizontalPadding = 20; // Left/right content margin in pixels
    private static readonly string FontFallbacks = ", Segoe UI Symbol, Segoe UI Emoji, Arial Unicode MS";
    private bool _followOutput = true;
    private int _lastScrollbackCount;
    private volatile bool _needsRender;
    private bool _scrollbarDragging;

    private string _cursorStyle = "bar";
    private bool _cursorBlink = true;

    // Render timer — fires at ~60fps independently of WPF's render schedule
    // to prevent the D3D11 swap chain from going stale (black screen).
    private System.Windows.Threading.DispatcherTimer? _renderTimer;

    // Cursor blink timer
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _cursorVisible = true;

    // Auto-scroll during selection drag
    private System.Windows.Threading.DispatcherTimer? _selectionAutoScrollTimer;
    private int _selectionAutoScrollDirection; // -1 = up, 1 = down, 0 = none

    // Visual bell
    private DateTime _bellFlashUntil;
    private System.Windows.Threading.DispatcherTimer? _bellTimer;


    // URL detection
    private (int row, int startCol, int endCol, string url)? _hoveredUrl;
    private int _lastUrlRow = -1;
    private List<(int startCol, int endCol, string url)>? _cachedRowUrls;

    // Search highlights
    private List<(int row, int col, int length)> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private HashSet<(int row, int col)>? _searchMatchSetCache;
    private HashSet<(int row, int col)>? _currentMatchSetCache;
    private static readonly HashSet<(int row, int col)> EmptyMatchSet = [];
    private readonly StringBuilder _inputLineBuffer = new();
    private bool _suppressNextEnterToShell;

    private bool _suppressNextEnterTextInput;

#pragma warning disable CS0414
    private bool _forceFullRedraw;
#pragma warning restore CS0414

    /// <summary>
    /// Converts a mouse position (WPF DIPs) to grid cell coordinates using the
    /// same pixel-rounded math as the GPU renderer, preventing progressive offset.
    /// </summary>
    private (int col, int row) PixelToCell(Point posDips)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int cellWPx = (int)Math.Round(_cellWidth * dpi);
        int cellHPx = (int)Math.Round(_cellHeight * dpi);
        double padPx = Math.Round(HorizontalPadding * dpi);
        if (cellWPx <= 0) cellWPx = 1;
        if (cellHPx <= 0) cellHPx = 1;
        int col = Math.Clamp((int)((posDips.X * dpi - padPx) / cellWPx), 0, _cols - 1);
        int row = Math.Clamp((int)(posDips.Y * dpi / cellHPx), 0, _rows - 1);
        return (col, row);
    }

    /// <summary>Fired when the pane wants focus.</summary>
    public event Action? FocusRequested;
    public event Action<string>? CommandSubmitted;
    public event Func<string, bool>? CommandInterceptRequested;
    public event Action? ClearRequested;
    public event Action<SplitDirection>? SplitRequested;
    public event Action? ZoomRequested;
    public event Action? ClosePaneRequested;
    public event Action? SearchRequested;

    /// <summary>Clears all event handlers (called before re-attaching to visual tree).</summary>
    public void ClearEventHandlers()
    {
        FocusRequested = null;
        CommandSubmitted = null;
        CommandInterceptRequested = null;
        ClearRequested = null;
        SplitRequested = null;
        ZoomRequested = null;
        ClosePaneRequested = null;
        SearchRequested = null;
    }

    /// <summary>Whether this pane has notification state (blue ring).</summary>
    public static readonly DependencyProperty HasNotificationProperty =
        DependencyProperty.Register(nameof(HasNotification), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnHasNotificationChanged));

    public bool HasNotification
    {
        get => (bool)GetValue(HasNotificationProperty);
        set => SetValue(HasNotificationProperty, value);
    }

    /// <summary>Whether this pane is focused.</summary>
    public static readonly DependencyProperty IsPaneFocusedProperty =
        DependencyProperty.Register(nameof(IsPaneFocused), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnIsPaneFocusedChanged));

    public bool IsPaneFocused
    {
        get => (bool)GetValue(IsPaneFocusedProperty);
        set => SetValue(IsPaneFocusedProperty, value);
    }

    /// <summary>Whether the parent surface is currently zoomed.</summary>
    public bool IsSurfaceZoomed { get; set; }

    public TerminalControl()
    {
        var settings = SettingsService.Current;
        var effectiveTheme = TerminalThemes.GetEffective(settings);
        _theme = new GhosttyTheme
        {
            Background = effectiveTheme.Background,
            Foreground = effectiveTheme.Foreground,
            Palette = effectiveTheme.Palette,
            SelectionBackground = effectiveTheme.SelectionBg,
            CursorColor = effectiveTheme.CursorColor,
            FontFamily = settings.FontFamily,
            FontSize = settings.FontSize
        };
        _renderHost = new D3DRenderHost();

        _fontSize = _theme.FontSize;
        // Font family with fallbacks for Unicode block/braille characters (QR codes, box drawing)
        _typeface = new Typeface(new FontFamily($"{_theme.FontFamily}, Segoe UI Symbol, Segoe UI Emoji, Arial Unicode MS"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        CalculateCellSize();

        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        AllowDrop = true;
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;

        _selection.SelectionChanged += () => { _forceFullRedraw = true; RequestRender(System.Windows.Threading.DispatcherPriority.Render); };

        // Cursor blink
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _cursorTimer.Tick += (_, _) =>
        {
            bool wasVisible = _cursorVisible;
            if (!_cursorBlink)
                _cursorVisible = true;
            else
                _cursorVisible = !_cursorVisible;

            if (_cursorVisible != wasVisible)
                RequestRender();
        };
        _cursorTimer.Start();
    }

    public void AttachSession(TerminalSession session)
    {
        if (_session != null)
        {
            _session.Redraw -= OnRedraw;
            _session.BellReceived -= OnBell;
        }

        _session = session;
        _inputLineBuffer.Clear();
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        _session.Redraw += OnRedraw;
        _session.BellReceived += OnBell;
        CalculateTerminalSize();
        _needsRender = true;
    }

    private void OnRedraw()
    {
        if (_session == null)
            return;

        var currentScrollback = _session.Buffer.ScrollbackCount;
        var scrollbackDelta = currentScrollback - _lastScrollbackCount;

        if (_followOutput || _scrollOffset == 0)
        {
            // Live mode: always stick to bottom.
            _scrollOffset = 0;
            _followOutput = true;
        }
        else if (_scrollOffset < 0 && scrollbackDelta > 0)
        {
            // Freeze viewport while output is streaming.
            _scrollOffset -= scrollbackDelta;
        }

        _scrollOffset = Math.Clamp(_scrollOffset, -currentScrollback, 0);
        if (_scrollOffset == 0)
            _followOutput = true;

        _lastScrollbackCount = currentScrollback;
        RequestRender();
    }

    private void OnBell()
    {
        _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(150);
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        _bellTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170),
        };
        // Restart the timer (handles rapid bell sequences)
        _bellTimer.Stop();
        _bellTimer.Tick -= OnBellTimerTick;
        _bellTimer.Tick += OnBellTimerTick;
        _bellTimer.Start();
    }

    private void OnBellTimerTick(object? sender, EventArgs e)
    {
        _bellTimer?.Stop();
        RequestRender();
    }

    // --- Search support ---

    public void SetSearchHighlights(List<(int row, int col, int length)> matches, int currentIndex)
    {
        _searchMatches = matches;
        _currentSearchMatch = currentIndex;
        RebuildSearchMatchCache();
        _forceFullRedraw = true;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void ClearSearchHighlights()
    {
        _searchMatches = [];
        _currentSearchMatch = -1;
        _searchMatchSetCache = null;
        _currentMatchSetCache = null;
        _forceFullRedraw = true;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RebuildSearchMatchCache()
    {
        var matchSet = new HashSet<(int row, int col)>();
        foreach (var (mRow, mCol, mLen) in _searchMatches)
        {
            for (int i = 0; i < mLen; i++)
                matchSet.Add((mRow, mCol + i));
        }
        _searchMatchSetCache = matchSet;

        if (_currentSearchMatch >= 0 && _currentSearchMatch < _searchMatches.Count)
        {
            var curSet = new HashSet<(int row, int col)>();
            var (cmRow, cmCol, cmLen) = _searchMatches[_currentSearchMatch];
            for (int i = 0; i < cmLen; i++)
                curSet.Add((cmRow, cmCol + i));
            _currentMatchSetCache = curSet;
        }
        else
        {
            _currentMatchSetCache = null;
        }
    }

    private void RequestRender(System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Background)
    {
        _needsRender = true;
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (_renderHost != null && !IsAncestorOf(_renderHost))
        {
            AddVisualChild(_renderHost);
            AddLogicalChild(_renderHost);
            _renderHost.RawInput += OnRenderHostRawInput;
        }

        // Use a DispatcherTimer instead of CompositionTarget.Rendering.
        // CompositionTarget.Rendering stops firing when WPF has no visual
        // changes, causing the D3D11 swap chain to go stale (black screen).
        if (_renderTimer == null)
        {
            _renderTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _renderTimer.Tick += OnRenderTimerTick;
        }
        _renderTimer.Start();
    }

    private void OnRenderTimerTick(object? sender, EventArgs e)
    {
        OnCompositionTargetRendering(sender, e);
    }

    /// <summary>
    /// Translates raw Win32 mouse messages from the D3D11 child HWND into
    /// WPF mouse coordinates and calls the appropriate handler directly.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    private void OnRenderHostRawInput(int msg, nint wParam, nint lParam)
    {
        // Convert screen position to child HWND client coords, then to WPF DIPs.
        // This avoids DPI double-conversion issues with PointFromScreen.
        {
            GetCursorPos(out var cursor);
            var origin = PointToScreen(new Point(0, 0));
            double localPxX = cursor.X - origin.X;
            double localPxY = cursor.Y - origin.Y;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var localDip = new Point(localPxX / dpi, localPxY / dpi);
            _rawMousePosition = localDip;
        }

        try
        {
        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN
            {
                Focus();
                FocusRequested?.Invoke();
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                };
                OnMouseLeftButtonDown(args);
                break;
            }
            case 0x0202: // WM_LBUTTONUP
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                };
                OnMouseLeftButtonUp(args);
                break;
            }
            case 0x0204: // WM_RBUTTONDOWN
            {
                Focus();
                FocusRequested?.Invoke();
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonDownEvent,
                };
                OnMouseRightButtonDown(args);
                break;
            }
            case 0x0200: // WM_MOUSEMOVE
            {
                var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = UIElement.MouseMoveEvent,
                };
                OnMouseMove(args);
                break;
            }
            case 0x020A: // WM_MOUSEWHEEL
            {
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                };
                OnMouseWheel(args);
                break;
            }
        }
        }
        finally
        {
            _rawMousePosition = null;
        }
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer?.Stop();
        _gpuRenderer?.Dispose();
        _gpuRenderer = null;
        // Do NOT dispose _renderHost here — its HWND must survive Loaded/Unloaded
        // cycles. Disposing it destroys the HWND, and a disposed HwndHost won't
        // recreate it when re-added to the visual tree (causing permanent black screen).
        _gpuInitialized = false;
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (_session == null) return;

        // Detect dead renderer (disposed by HandleDeviceLost) and force reinitialization
        if (_gpuRenderer != null && !_gpuRenderer.IsInitialized)
        {
            _gpuRenderer.Dispose();
            _gpuRenderer = null;
            _gpuInitialized = false;
        }

        // Lazy GPU init
        if (!_gpuInitialized && _renderHost?.Hwnd != nint.Zero)
        {
            try
            {
                InitializeGpuRenderer();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GPU] InitializeGpuRenderer failed: {ex.Message}");
                _gpuRenderer?.Dispose();
                _gpuRenderer = null;
            }
            _gpuInitialized = _gpuRenderer != null;
        }
        if (_gpuRenderer == null || !_gpuRenderer.IsInitialized) return;

        // Skip rendering when nothing has changed and no animation is active.
        // This prevents DWM from recompositing the desktop every 16ms (~60fps),
        // which causes visible brightness flickering on the entire screen.
        bool bellActive = DateTime.UtcNow < _bellFlashUntil;
        if (!_needsRender && !bellActive)
            return;
        _needsRender = false;

        // Ensure swap chain dimensions match current control size.
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int expectedW = (int)(ActualWidth * dpi);
            int expectedH = (int)(ActualHeight * dpi);
            if (expectedW > 0 && expectedH > 0 &&
                (expectedW != _gpuRenderer.PixelWidth || expectedH != _gpuRenderer.PixelHeight))
            {
                try
                {
                    _gpuRenderer.ResizeSwapChain(expectedW, expectedH, _cols, _rows, (float)_cellWidth, (float)_cellHeight);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GPU] Dimension fix resize failed: {ex.Message}");
                    _gpuRenderer.Dispose();
                    _gpuRenderer = null;
                    _gpuInitialized = false;
                    return;
                }
            }
        }

        float cursorAlpha = (_cursorVisible || !_cursorBlink) && IsPaneFocused ? 1f : 0f;
        float bellAlpha = DateTime.UtcNow < _bellFlashUntil
            ? (float)(_bellFlashUntil - DateTime.UtcNow).TotalMilliseconds / 150f
            : 0f;

        int scrollbackOffset = _scrollOffset;

        try
        {
            _gpuRenderer.Render(
                _session, _scrollOffset, _rows,
                cursorAlpha, bellAlpha,
                _selection, scrollbackOffset,
                _searchMatchSetCache, _currentMatchSetCache,
                _hoveredUrl, _cursorStyle, _cursorVisible);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GPU] Render failed, reinitializing: {ex.Message}");
            _gpuRenderer.Dispose();
            _gpuRenderer = null;
            _gpuInitialized = false;
            _needsRender = true;
            return;
        }

        _forceFullRedraw = false;
    }

    private void InitializeGpuRenderer()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int pw = (int)(ActualWidth * dpi);
        int ph = (int)(ActualHeight * dpi);
        if (pw <= 0 || ph <= 0) return;

        _gpuRenderer = new D3DTerminalRenderer();
        _gpuRenderer.Initialize(
            _renderHost!.Hwnd, pw, ph,
            _theme.FontFamily, (float)_fontSize, (float)dpi,
            _cols, _rows, (float)_cellWidth, (float)_cellHeight,
            _theme);
    }

    // --- Layout ---

    private void CalculateCellSize()
    {
        var formattedText = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _cellWidth = formattedText.WidthIncludingTrailingWhitespace;
        _cellHeight = formattedText.Height;
    }

    private void CalculateTerminalSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _cellWidth <= 0 || _cellHeight <= 0) return;

        int cols = Math.Max(1, (int)((ActualWidth - HorizontalPadding - 10) / _cellWidth));
        int rows = Math.Max(1, (int)(ActualHeight / _cellHeight));

        if (cols != _cols || rows != _rows)
        {
            _cols = cols;
            _rows = rows;
            _forceFullRedraw = true;
            _session?.Resize(cols, rows);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _renderHost?.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _renderHost?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        CalculateCellSize();
        CalculateTerminalSize();

        if (_gpuRenderer?.IsInitialized == true)
        {
            _gpuRenderer.UpdateFont(
                _theme.FontFamily,
                (float)_fontSize,
                (float)newDpi.PixelsPerDip,
                (float)_cellWidth,
                (float)_cellHeight);

            int pw = (int)(ActualWidth * newDpi.PixelsPerDip);
            int ph = (int)(ActualHeight * newDpi.PixelsPerDip);
            if (pw > 0 && ph > 0)
                _gpuRenderer.ResizeSwapChain(pw, ph, _cols, _rows, (float)_cellWidth, (float)_cellHeight);
        }

        _forceFullRedraw = true;
        _needsRender = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _forceFullRedraw = true;
        CalculateTerminalSize();

        if (_gpuRenderer?.IsInitialized == true)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int pw = (int)(ActualWidth * dpi);
            int ph = (int)(ActualHeight * dpi);
            if (pw > 0 && ph > 0)
                _gpuRenderer.ResizeSwapChain(pw, ph, _cols, _rows, (float)_cellWidth, (float)_cellHeight);
        }

        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    // --- Mouse reporting ---

    private bool IsMouseTrackingActive =>
        _session?.Buffer.MouseEnabled == true;

    private void SendMouseReport(int button, int col, int row, bool press)
    {
        if (_session == null) return;
        var buf = _session.Buffer;
        if (!buf.MouseEnabled) return;

        col = Math.Clamp(col, 0, buf.Cols - 1);
        row = Math.Clamp(row, 0, buf.Rows - 1);

        if (buf.MouseSgrExtended)
        {
            char suffix = press ? 'M' : 'm';
            _session.Write($"\x1b[<{button};{col + 1};{row + 1}{suffix}");
        }
        else if (press)
        {
            char cb = (char)(button + 32);
            char cx = (char)(col + 33);
            char cy = (char)(row + 33);
            _session.Write($"\x1b[M{cb}{cx}{cy}");
        }
    }

    // --- Keyboard input ---

    private void EnsureLiveView()
    {
        if (_session == null)
            return;

        if (_scrollOffset == 0 && _followOutput)
            return;

        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void TrackInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\b':
                    if (_inputLineBuffer.Length > 0)
                        _inputLineBuffer.Length--;
                    break;

                case '\r':
                case '\n':
                    SubmitBufferedCommand(allowInterception: false);
                    break;

                default:
                    if (!char.IsControl(ch))
                    {
                        _inputLineBuffer.Append(ch);

                        if (_inputLineBuffer.Length > 4096)
                            _inputLineBuffer.Remove(0, _inputLineBuffer.Length - 4096);
                    }
                    break;
            }
        }
    }

    private void SubmitBufferedCommand(bool allowInterception)
    {
        var rawCommand = _inputLineBuffer.ToString();
        var command = rawCommand.Trim();
        _inputLineBuffer.Clear();

        if (string.IsNullOrWhiteSpace(command))
            return;

        if (allowInterception && TryInterceptCommand(command))
        {
            _suppressNextEnterToShell = true;
            _suppressNextEnterTextInput = true;

            // The command text has already been sent character-by-character to the shell.
            // Cancel the current input line so a subsequent newline from agent output
            // cannot execute the intercepted handler command.
            if (_session != null)
                _session.Write("\x03");
            return;
        }

        CommandSubmitted?.Invoke(command);
    }

    private bool TryInterceptCommand(string command)
    {
        var handlers = CommandInterceptRequested;
        if (handlers == null)
            return false;

        foreach (var callback in handlers.GetInvocationList().OfType<Func<string, bool>>())
        {
            try
            {
                if (callback(command))
                    return true;
            }
            catch
            {
                // Ignore handler failures to avoid breaking terminal input.
            }
        }

        return false;
    }

    private bool CopySelectionToClipboard()
    {
        if (_session == null || !_selection.HasSelection)
            return false;

        var text = _selection.GetSelectedText(_session.Buffer);
        if (string.IsNullOrEmpty(text))
            return false;

        Clipboard.SetText(text);
        _selection.ClearSelection();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);

        // Let application-level shortcuts bubble to MainWindow.
        // Ctrl+Alt combos (pane focus), Ctrl+Tab (surface cycling),
        // and Ctrl+Shift combos (split, zoom, search, etc.) are app-level.
        if (ctrl && alt) return;
        if (ctrl && shift) return;
        if (ctrl && e.Key == Key.Tab) return;

        // Ctrl+Backspace: delete previous word (send Ctrl+W / unix-word-rubout)
        if (ctrl && e.Key == Key.Back)
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write("\x17");
            e.Handled = true;
            return;
        }

        // Terminal shortcuts
        if (ctrl && e.Key == Key.C)
        {
            if (!CopySelectionToClipboard())
            {
                // Forward Ctrl+C to shell as interrupt when no selection is active.
                _inputLineBuffer.Clear();
                EnsureLiveView();
                _session.Write("\x03");
            }

            e.Handled = true;
            return;
        }

        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Insert)
        {
            _ = CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        // Forward Ctrl+letter as control bytes (e.g. Ctrl+X => 0x18) for TUI apps like nano.
        if (ctrl && !modifiers.HasFlag(ModifierKeys.Alt) && TryGetCtrlLetterSequence(e.Key, out var ctrlSequence))
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write(ctrlSequence);
            e.Handled = true;
            return;
        }

        bool appCursor = _session.Buffer.ApplicationCursorKeys;
        string? sequence = KeyToVtSequence(e.Key, modifiers, appCursor);
        if (sequence != null)
        {
            if (e.Key == Key.Back)
                TrackInputText("\b");
            else if (e.Key == Key.Enter)
            {
                SubmitBufferedCommand(allowInterception: true);
                if (_suppressNextEnterToShell)
                {
                    _suppressNextEnterToShell = false;
                    e.Handled = true;
                    return;
                }
            }

            EnsureLiveView();
            _session.Write(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        // KeyDown handles Enter; suppress the trailing TextInput CR/LF when
        // an intercepted command consumed the shell submission.
        if (_suppressNextEnterTextInput && (e.Text.Contains('\r') || e.Text.Contains('\n')))
        {
            _suppressNextEnterTextInput = false;
            e.Handled = true;
            return;
        }

        // Prevent duplicate newline writes from TextInput path.
        if (e.Text.Contains('\r') || e.Text.Contains('\n'))
        {
            e.Handled = true;
            return;
        }

        // Handle Ctrl+C (copy when selection exists)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x03")
        {
            if (_selection.HasSelection)
            {
                var text = _selection.GetSelectedText(_session.Buffer);
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
                _selection.ClearSelection();
                return;
            }
        }

        // Handle Ctrl+V (paste)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x16")
        {
            PasteFromClipboard();
            return;
        }

        EnsureLiveView();
        TrackInputText(e.Text);
        _session.Write(e.Text);
        _selection.ClearSelection();
    }

    private void PasteFromClipboard()
    {
        if (_session == null) return;
        if (!TryGetClipboardPasteText(out var text)) return;

        PasteText(text);
    }

    private void PasteText(string text)
    {
        if (_session == null || string.IsNullOrEmpty(text)) return;

        EnsureLiveView();
        TrackInputText(text);

        if (_session.Buffer.BracketedPasteMode)
            _session.Write("\x1b[200~" + text + "\x1b[201~");
        else
            _session.Write(text);
    }

    private static bool HasClipboardPasteContent()
    {
        try
        {
            return Clipboard.ContainsText()
                || Clipboard.ContainsFileDropList()
                || Clipboard.ContainsImage();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClipboardPasteText(out string text)
    {
        text = string.Empty;

        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
                return !string.IsNullOrEmpty(text);
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var paths = files.Cast<string>()
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();

                if (paths.Length > 0)
                {
                    text = string.Join(" ", paths.Select(QuotePathForShell));
                    return true;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    var tempPath = SaveBitmapSourceToTempFile(image);
                    if (!string.IsNullOrWhiteSpace(tempPath))
                    {
                        text = QuotePathForShell(tempPath);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore clipboard race/format exceptions and treat as unavailable.
        }

        return false;
    }

    private static string? SaveBitmapSourceToTempFile(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "cmux", "clipboard-images");
            Directory.CreateDirectory(dir);

            var fileName = $"cmux-clipboard-{DateTime.Now:yyyyMMdd-HHmmssfff}.png";
            var fullPath = Path.Combine(dir, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var stream = File.Create(fullPath);
            encoder.Save(stream);

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    private static string QuotePathForShell(string path)
    {
        if (path.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0)
            return path;

        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static bool HasDropContent(IDataObject? data)
    {
        if (data == null)
            return false;

        try
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent("PNG");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDropPasteText(IDataObject? data, out string text)
    {
        text = string.Empty;
        if (data == null)
            return false;

        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                text = string.Join(" ", files
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(QuotePathForShell));
                return !string.IsNullOrWhiteSpace(text);
            }

            if (data.GetDataPresent(DataFormats.UnicodeText) &&
                data.GetData(DataFormats.UnicodeText) is string unicodeText &&
                !string.IsNullOrEmpty(unicodeText))
            {
                text = unicodeText;
                return true;
            }

            if (data.GetDataPresent(DataFormats.Text) &&
                data.GetData(DataFormats.Text) is string plainText &&
                !string.IsNullOrEmpty(plainText))
            {
                text = plainText;
                return true;
            }

            if (TryGetDropBitmapSource(data, out var bitmap))
            {
                var tempPath = SaveBitmapSourceToTempFile(bitmap);
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    text = QuotePathForShell(tempPath);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore drag-data conversion failures.
        }

        return false;
    }

    private static bool TryGetDropBitmapSource(IDataObject data, out BitmapSource bitmap)
    {
        bitmap = null!;

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var value = data.GetData(DataFormats.Bitmap);
            if (value is BitmapSource bitmapSource)
            {
                bitmap = bitmapSource;
                return true;
            }
        }

        if (data.GetDataPresent("PNG"))
        {
            var value = data.GetData("PNG");
            if (value is MemoryStream memoryStream)
            {
                memoryStream.Position = 0;
                var frame = BitmapFrame.Create(memoryStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }

            if (value is byte[] bytes && bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }
        }

        return false;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        Focus();
        FocusRequested?.Invoke();

        if (_session != null && TryGetDropPasteText(e.Data, out var text))
        {
            PasteText(text);
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Returns the mouse position in WPF DIPs relative to this control.
    /// Always computes from Win32 GetCursorPos because WPF's mouse tracking
    /// is stale (the HwndHost child HWND captures WM_MOUSEMOVE).
    /// </summary>
    private Point GetMousePos(MouseEventArgs e)
    {
        if (_rawMousePosition.HasValue)
            return _rawMousePosition.Value;

        // Fallback: always use GetCursorPos (never WPF's stale e.GetPosition)
        GetCursorPos(out var pt);
        var origin = PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new Point((pt.X - origin.X) / dpi, (pt.Y - origin.Y) / dpi);
    }

    // --- Mouse input ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        FocusRequested?.Invoke();

        if (_cols <= 0 || _rows <= 0) return;

        var pos = GetMousePos(e);

        // Scrollbar click/drag — check before cell hit-testing
        // Hit area is wider (20px) than the visual scrollbar (6px) for easier grabbing
        if (_session != null && _session.Buffer.ScrollbackCount > 0)
        {
            // Generous hit area — rightmost 60 DIPs of the terminal
            double hitX = ActualWidth - 60;
            if (pos.X >= hitX)
            {
                int scrollbackCount = _session.Buffer.ScrollbackCount;
                int totalLines = scrollbackCount + _rows;
                double thumbRatio = (double)_rows / totalLines;
                double thumbHeight = Math.Max(20, ActualHeight * thumbRatio);
                double scrollFraction = Math.Clamp(pos.Y / Math.Max(1, ActualHeight - thumbHeight), 0, 1);
                int viewStartLine = (int)(scrollFraction * (totalLines - _rows));
                _scrollOffset = Math.Clamp(viewStartLine - scrollbackCount, -scrollbackCount, 0);
                _followOutput = _scrollOffset == 0;
                _lastScrollbackCount = scrollbackCount;

                _scrollbarDragging = true;
                CaptureMouse();
                _forceFullRedraw = true;
                RequestRender(System.Windows.Threading.DispatcherPriority.Render);
                e.Handled = true;
                return;
            }
        }

        var (col, row) = PixelToCell(pos);

        // Ctrl+Click for URL opening
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _hoveredUrl.HasValue)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_hoveredUrl.Value.url) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
            return;
        }

        // Mouse reporting (bypass selection when app requests mouse)
        if (IsMouseTrackingActive)
        {
            SendMouseReport(0, col, row, true);
            _mouseDown = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && _session != null)
        {
            _selection.SelectWord(_session.Buffer, row, col, _scrollOffset);
        }
        else if (e.ClickCount == 3 && _session != null)
        {
            _selection.SelectLine(row, _session.Buffer.Cols, _scrollOffset, _session.Buffer.ScrollbackCount);
        }
        else
        {
            _selection.StartSelection(row, col, _scrollOffset, _session?.Buffer.ScrollbackCount ?? 0);
            _mouseDown = true;
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_cols <= 0 || _rows <= 0) return;

        var pos = GetMousePos(e);

        // Scrollbar drag
        if (_scrollbarDragging && _session != null)
        {
            int scrollbackCount = _session.Buffer.ScrollbackCount;
            int totalLines = scrollbackCount + _rows;
            double thumbRatio = (double)_rows / totalLines;
            double thumbHeight = Math.Max(20, ActualHeight * thumbRatio);
            double scrollFraction = Math.Clamp(pos.Y / Math.Max(1, ActualHeight - thumbHeight), 0, 1);
            int viewStartLine = (int)(scrollFraction * (totalLines - _rows));
            _scrollOffset = Math.Clamp(viewStartLine - scrollbackCount, -scrollbackCount, 0);
            _followOutput = _scrollOffset == 0;
            _lastScrollbackCount = scrollbackCount;
            _forceFullRedraw = true;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        var (col, row) = PixelToCell(pos);

        // URL detection (Ctrl held) — cache scanned URLs per row
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _session != null && row < _session.Buffer.Rows)
        {
            // Only re-scan when the row changes
            if (row != _lastUrlRow)
            {
                _lastUrlRow = row;
                var lineText = UrlDetector.GetRowText(_session.Buffer, row);
                _cachedRowUrls = UrlDetector.FindUrls(lineText);
            }

            // Check cached URLs for hit at current column
            var oldHover = _hoveredUrl;
            _hoveredUrl = null;
            if (_cachedRowUrls != null)
            {
                foreach (var (startCol, endCol, url) in _cachedRowUrls)
                {
                    if (col >= startCol && col <= endCol)
                    {
                        _hoveredUrl = (row, startCol, endCol, url);
                        break;
                    }
                }
            }

            Cursor = _hoveredUrl.HasValue ? Cursors.Hand : Cursors.Arrow;
            if (_hoveredUrl != oldHover)
            {
                _forceFullRedraw = true;
                RequestRender(System.Windows.Threading.DispatcherPriority.Render);
            }
        }
        else if (_hoveredUrl.HasValue)
        {
            _hoveredUrl = null;
            _lastUrlRow = -1;
            _cachedRowUrls = null;
            Cursor = Cursors.Arrow;
            _forceFullRedraw = true;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }

        // Mouse reporting (motion events)
        if (IsMouseTrackingActive && _mouseDown)
        {
            var buf = _session!.Buffer;
            if (buf.MouseTrackingButton || buf.MouseTrackingAny)
            {
                SendMouseReport(32, col, row, true); // 32 = motion flag
            }
            return;
        }
        if (IsMouseTrackingActive && _session!.Buffer.MouseTrackingAny)
        {
            SendMouseReport(35, col, row, true); // 35 = no-button motion
            return;
        }

        // Selection drag with auto-scroll at edges
        if (_mouseDown && !IsMouseTrackingActive)
        {
            _selection.ExtendSelection(row, col, _scrollOffset, _session?.Buffer.ScrollbackCount ?? 0);

            // Auto-scroll when mouse is near top/bottom edge
            if (pos.Y < 0)
                StartSelectionAutoScroll(-1);
            else if (pos.Y > ActualHeight)
                StartSelectionAutoScroll(1);
            else
                StopSelectionAutoScroll();
        }
    }

    private void StartSelectionAutoScroll(int direction)
    {
        _selectionAutoScrollDirection = direction;
        if (_selectionAutoScrollTimer != null) return;
        _selectionAutoScrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _selectionAutoScrollTimer.Tick += (_, _) =>
        {
            if (_session == null || _selectionAutoScrollDirection == 0) return;
            int scrollbackCount = _session.Buffer.ScrollbackCount;
            _scrollOffset = Math.Clamp(_scrollOffset + _selectionAutoScrollDirection, -scrollbackCount, 0);
            _followOutput = _scrollOffset == 0;
            _lastScrollbackCount = scrollbackCount;

            // Extend selection to edge row
            int edgeRow = _selectionAutoScrollDirection < 0 ? 0 : _rows - 1;
            int edgeCol = _selectionAutoScrollDirection < 0 ? 0 : _cols - 1;
            _selection.ExtendSelection(edgeRow, edgeCol, _scrollOffset, scrollbackCount);

            _forceFullRedraw = true;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        };
        _selectionAutoScrollTimer.Start();
    }

    private void StopSelectionAutoScroll()
    {
        _selectionAutoScrollDirection = 0;
        _selectionAutoScrollTimer?.Stop();
        _selectionAutoScrollTimer = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        StopSelectionAutoScroll();

        if (_scrollbarDragging)
        {
            _scrollbarDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (IsMouseTrackingActive && _mouseDown && _cols > 0 && _rows > 0)
        {
            var pos = GetMousePos(e);
            int col = Math.Clamp((int)((pos.X - HorizontalPadding) / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(0, col, row, false);
        }

        if (_mouseDown)
        {
            _mouseDown = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = GetMousePos(e);
            int col = Math.Clamp((int)((pos.X - HorizontalPadding) / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(2, col, row, true);
            return;
        }

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };

        var menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE9))));
        menuItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        menuItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        menuItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));

        var separatorStyle = new Style(typeof(Separator));
        separatorStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C))));
        separatorStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2)));

        menu.Resources.Add(typeof(MenuItem), menuItemStyle);
        menu.Resources.Add(typeof(Separator), separatorStyle);

        // Copy
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copyItem.Icon = MakeIcon("\uE8C8");
        copyItem.IsEnabled = _selection.HasSelection;
        copyItem.Click += (_, _) =>
        {
            if (_session != null)
            {
                var text = _selection.GetSelectedText(_session.Buffer);
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                _selection.ClearSelection();
            }
        };
        menu.Items.Add(copyItem);

        // Paste
        var pasteItem = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V" };
        pasteItem.Icon = MakeIcon("\uE77F");
        pasteItem.IsEnabled = HasClipboardPasteContent();
        pasteItem.Click += (_, _) => PasteFromClipboard();
        menu.Items.Add(pasteItem);

        // Select All
        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Icon = MakeIcon("\uE8B3");
        selectAllItem.Click += (_, _) =>
        {
            if (_session != null)
                _selection.SelectAll(_session.Buffer.Rows, _session.Buffer.Cols, _scrollOffset, _session.Buffer.ScrollbackCount);
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new Separator());

        // Split Right
        var splitRight = new MenuItem { Header = "Split Right", InputGestureText = "Ctrl+D" };
        splitRight.Icon = MakeIcon("\uE745");
        splitRight.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Vertical);
        menu.Items.Add(splitRight);

        // Split Down
        var splitDown = new MenuItem { Header = "Split Down", InputGestureText = "Ctrl+Shift+D" };
        splitDown.Icon = MakeIcon("\uE74B");
        splitDown.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Horizontal);
        menu.Items.Add(splitDown);

        menu.Items.Add(new Separator());

        // Zoom
        var isZoomed = IsSurfaceZoomed;
        var zoom = new MenuItem
        {
            Header = isZoomed ? "Unzoom Pane" : "Zoom Pane",
            InputGestureText = "Ctrl+Shift+Z",
            IsCheckable = true,
            IsChecked = isZoomed,
        };
        zoom.Icon = MakeIcon(isZoomed ? "\uE73F" : "\uE740");
        zoom.Click += (_, _) => ZoomRequested?.Invoke();
        menu.Items.Add(zoom);

        // Close Pane
        var closePane = new MenuItem { Header = "Close Pane" };
        closePane.Icon = MakeIcon("\uE711");
        closePane.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        closePane.Click += (_, _) => ClosePaneRequested?.Invoke();
        menu.Items.Add(closePane);

        menu.Items.Add(new Separator());

        // Clear Terminal
        var clear = new MenuItem { Header = "Clear Terminal" };
        clear.Icon = MakeIcon("\uE894");
        clear.Click += (_, _) =>
        {
            ClearRequested?.Invoke();
            ClearTerminalView();
        };
        menu.Items.Add(clear);

        // Search
        var search = new MenuItem { Header = "Search", InputGestureText = "Ctrl+Shift+F" };
        search.Icon = MakeIcon("\uE721");
        search.Click += (_, _) => SearchRequested?.Invoke();
        menu.Items.Add(search);

        menu.Items.Add(new Separator());

        // Open in Windows Explorer
        var openExplorerItem = new MenuItem { Header = "Open in Windows Explorer" };
        openExplorerItem.Icon = MakeIcon("\uE838");
        openExplorerItem.Click += (_, _) =>
        {
            var cwd = _session?.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(cwd) && System.IO.Directory.Exists(cwd))
                System.Diagnostics.Process.Start("explorer.exe", cwd);
        };
        menu.Items.Add(openExplorerItem);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static TextBlock MakeIcon(string glyph) =>
        new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 };

    private void ClearTerminalView()
    {
        if (_session == null) return;

        _session.Buffer.EraseInDisplay(3);
        _session.Buffer.MoveCursorTo(0, 0);
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        // Ask shell to repaint prompt where supported.
        _session.Write("\x0c");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;

        // Mouse wheel reporting
        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = GetMousePos(e);
            int col = Math.Clamp((int)((pos.X - HorizontalPadding) / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            int button = e.Delta > 0 ? 64 : 65; // 64 = scroll up, 65 = scroll down
            SendMouseReport(button, col, row, true);
            e.Handled = true;
            return;
        }

        // Scrollback navigation (WezTerm-style remainder accumulation)
        // Negate delta: WPF Delta>0 = scroll up = go into history = negative _scrollOffset
        int scrollLines = (int)SystemParameters.WheelScrollLines;
        int scaledDelta = -e.Delta * scrollLines;

        // Reset remainder on direction change
        if (_scrollWheelRemainder != 0 && Math.Sign(_scrollWheelRemainder) != Math.Sign(scaledDelta))
            _scrollWheelRemainder = 0;

        _scrollWheelRemainder += scaledDelta;
        int lines = _scrollWheelRemainder / 120; // 120 = WHEEL_DELTA
        _scrollWheelRemainder %= 120;

        if (lines != 0)
        {
            _scrollOffset = Math.Clamp(_scrollOffset + lines, -_session.Buffer.ScrollbackCount, 0);
            _followOutput = _scrollOffset == 0;
            _lastScrollbackCount = _session.Buffer.ScrollbackCount;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }
        e.Handled = true;
    }

    // --- Visual tree ---

    protected override int VisualChildrenCount => _renderHost != null ? 1 : 0;

    protected override Visual GetVisualChild(int index)
    {
        if (index == 0 && _renderHost != null) return _renderHost;
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static bool TryGetCtrlLetterSequence(Key key, out string sequence)
    {
        sequence = "";
        if (key < Key.A || key > Key.Z)
            return false;

        var controlCode = (char)(key - Key.A + 1);
        sequence = controlCode.ToString();
        return true;
    }

    private static string? KeyToVtSequence(Key key, ModifierKeys modifiers, bool appCursor)
    {
        if (appCursor)
        {
            var appSeq = key switch
            {
                Key.Up => "\x1bOA",
                Key.Down => "\x1bOB",
                Key.Right => "\x1bOC",
                Key.Left => "\x1bOD",
                Key.Home => "\x1bOH",
                Key.End => "\x1bOF",
                _ => (string?)null,
            };
            if (appSeq != null) return appSeq;
        }

        return key switch
        {
            Key.Enter => "\r",
            Key.Escape => "\x1b",
            Key.Back => "\x7f",
            Key.Tab => modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
    }

    public void UpdateTheme(GhosttyTheme theme)
    {
        _theme = theme;
        _typeface = new Typeface(new FontFamily(theme.FontFamily + FontFallbacks), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = theme.FontSize;
        _forceFullRedraw = true;
        CalculateCellSize();
        CalculateTerminalSize();
        _gpuRenderer?.UpdateTheme(theme);
        _gpuRenderer?.UpdateFont(theme.FontFamily, (float)_fontSize,
            (float)VisualTreeHelper.GetDpi(this).PixelsPerDip, (float)_cellWidth, (float)_cellHeight);
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void UpdateSettings(TerminalTheme theme, string fontFamily, int fontSize)
    {
        // Convert TerminalTheme to GhosttyTheme
        var ghosttyTheme = new GhosttyTheme
        {
            Background = theme.Background,
            Foreground = theme.Foreground,
            Palette = theme.Palette,
            SelectionBackground = theme.SelectionBg,
            CursorColor = theme.CursorColor,
            FontFamily = fontFamily,
            FontSize = fontSize
        };
        UpdateSettings(ghosttyTheme, fontFamily, fontSize);
    }

    public void UpdateSettings(GhosttyTheme theme, string fontFamily, int fontSize)
    {
        _theme = theme;
        _fontSize = fontSize;

        var settings = SettingsService.Current;
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        _typeface = new Typeface(new FontFamily(fontFamily + FontFallbacks), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _forceFullRedraw = true;
        CalculateCellSize();
        CalculateTerminalSize();
        _gpuRenderer?.UpdateTheme(theme);
        _gpuRenderer?.UpdateFont(fontFamily, (float)_fontSize,
            (float)VisualTreeHelper.GetDpi(this).PixelsPerDip, (float)_cellWidth, (float)_cellHeight);
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnHasNotificationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TerminalControl)d).RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnIsPaneFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (TerminalControl)d;
        if ((bool)e.NewValue)
        {
            ctrl._cursorVisible = true;
            if (ctrl._cursorBlink)
                ctrl._cursorTimer?.Start();
        }
        ctrl.RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }
}
