using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Cmux.Core.Models;
using Cmux.Core.Terminal;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cmux.Rendering;

/// <summary>
/// Main D3D11 rendering orchestrator: initializes the device, swap chain, and shader
/// pipeline, then drives per-frame cell population and instanced draw calls.
/// </summary>
internal sealed class D3DTerminalRenderer : IDisposable
{
    // ── Constant buffer layout (must match HLSL cbuffer) ────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantBufferData
    {
        public Vector2 CellSize;
        public Vector2 GridSize;
        public Vector2 AtlasSize;
        public Vector2 ViewportSize;
        public Vector2 GridOffset;
        public float CursorAlpha;
        public float BellAlpha;
    }

    // ── D3D11 / DXGI objects ────────────────────────────────────────
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11SamplerState? _sampler;

    // ── Rendering resources ─────────────────────────────────────────
    private GlyphAtlas? _atlas;
    private CellBuffer? _cellBuffer;

    // ── Dimensions and configuration ────────────────────────────────
    private int _pixelWidth;
    private int _pixelHeight;
    private int _cols;
    private int _rows;
    private float _cellWidth;
    private float _cellHeight;
    private float _fontSize;
    private float _dpi;
    private GhosttyTheme _theme = new();
    private bool _disposed;

    private const float HorizontalPadding = 20f;

    // Atlas texture is always 2048x2048 (matches GlyphAtlas internal constant).
    private const float AtlasSize = 2048f;

    /// <summary>Whether the renderer has been successfully initialized.</summary>
    public bool IsInitialized => _device != null;

    // ── Initialization ──────────────────────────────────────────────

    /// <summary>
    /// Creates the D3D11 device, swap chain, shaders, and GPU resources.
    /// </summary>
    public void Initialize(
        nint hwnd, int pixelWidth, int pixelHeight,
        string fontFamily, float fontSize, float dpi,
        int cols, int rows, float cellWidth, float cellHeight,
        GhosttyTheme theme)
    {
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _cols = cols;
        _rows = rows;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _fontSize = fontSize;
        _dpi = dpi;
        _theme = theme;

        // 1. Create D3D11 device + immediate context
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out _device,
            out _context).CheckError();

        // 2. Query DXGI chain: Device -> Adapter -> Factory2
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // 3. Create swap chain for the HWND
        var scDesc = new SwapChainDescription1
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, scDesc);
        CreateRenderTarget();

        // 4. Compile shaders
        _vertexShader = ShaderLoader.CreateVertexShader(_device, out var vsBytecode);
        vsBytecode.Dispose();
        _pixelShader = ShaderLoader.CreatePixelShader(_device);

        // 5. Constant buffer (dynamic, 16-byte aligned)
        int cbSize = (Marshal.SizeOf<ConstantBufferData>() + 15) & ~15;
        _constantBuffer = _device.CreateBuffer(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        // 6. Point sampler (pixel-perfect glyph rendering)
        _sampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint,
            TextureAddressMode.Clamp));

        // 7. Create atlas and cell buffer
        _atlas = new GlyphAtlas(_device, _context, fontFamily, fontSize, dpi);
        _cellBuffer = new CellBuffer(_device, cols, rows);
    }

    // ── Render ──────────────────────────────────────────────────────

    /// <summary>
    /// Populates the CellBuffer from the terminal viewport, then executes
    /// the GPU pipeline (one instanced draw call for the entire grid).
    /// </summary>
    public void Render(
        TerminalSession session, int scrollOffset, int viewRows,
        float cursorAlpha, float bellAlpha,
        TerminalSelection selection, int scrollbackOffset,
        HashSet<(int row, int col)>? searchMatchSet,
        HashSet<(int row, int col)>? currentMatchSet,
        (int row, int startCol, int endCol, string url)? hoveredUrl,
        string cursorStyle, bool cursorVisible)
    {
        if (_disposed || _device == null || _context == null ||
            _swapChain == null || _rtv == null ||
            _atlas == null || _cellBuffer == null)
            return;

        var buffer = session.Buffer;
        int scrollbackCount = buffer.ScrollbackCount;
        int viewStartLine = scrollbackCount + scrollOffset;

        // ── Populate CellBuffer under render lock ───────────────────
        lock (session.RenderLock)
        {
            for (int visRow = 0; visRow < _rows; visRow++)
            {
                int virtualLine = viewStartLine + visRow;
                bool isScrollback = virtualLine < scrollbackCount;
                int bufferRow = virtualLine - scrollbackCount;

                TerminalCell[]? scrollbackLine = null;
                if (isScrollback)
                    scrollbackLine = buffer.GetScrollbackLine(virtualLine);

                for (int col = 0; col < _cols; col++)
                {
                    var cell = GetCell(col, isScrollback, scrollbackLine, bufferRow, buffer);
                    var attr = cell.Attribute;
                    bool isInverse = attr.Flags.HasFlag(CellFlags.Inverse);

                    // Resolve foreground / background (same logic as TerminalControl.RenderRow)
                    var cellFg = isInverse
                        ? (attr.Background.IsDefault ? _theme.Background : attr.Background)
                        : attr.Foreground;
                    var cellBg = isInverse
                        ? (attr.Foreground.IsDefault ? _theme.Foreground : attr.Foreground)
                        : attr.Background;

                    var fg = cellFg.IsDefault ? _theme.Foreground : cellFg;
                    var bg = cellBg;

                    // Atlas lookup
                    GlyphInfo glyphInfo = default;
                    bool hasChar = cell.Character != '\0' && cell.Character != ' ';
                    if (hasChar)
                    {
                        var style = GlyphStyle.Regular;
                        if (attr.Flags.HasFlag(CellFlags.Bold) && attr.Flags.HasFlag(CellFlags.Italic))
                            style = GlyphStyle.BoldItalic;
                        else if (attr.Flags.HasFlag(CellFlags.Bold))
                            style = GlyphStyle.Bold;
                        else if (attr.Flags.HasFlag(CellFlags.Italic))
                            style = GlyphStyle.Italic;

                        glyphInfo = _atlas.GetOrRasterize(cell.Character, style);
                    }

                    // Build flags bitfield
                    uint flags = 0;
                    if (attr.Flags.HasFlag(CellFlags.Underline))     flags |= CellData.FLAG_UNDERLINE;
                    if (attr.Flags.HasFlag(CellFlags.Strikethrough)) flags |= CellData.FLAG_STRIKETHROUGH;
                    if (attr.Flags.HasFlag(CellFlags.Dim))           flags |= CellData.FLAG_DIM;
                    if (cell.Width == 2)                              flags |= CellData.FLAG_WIDE;

                    // Selection
                    if (selection.HasSelection &&
                        selection.IsSelected(visRow, col, scrollbackOffset, scrollbackCount))
                        flags |= CellData.FLAG_SELECTED;

                    // Search highlights
                    if (searchMatchSet != null && searchMatchSet.Contains((visRow, col)))
                        flags |= CellData.FLAG_SEARCH_MATCH;
                    if (currentMatchSet != null && currentMatchSet.Contains((visRow, col)))
                        flags |= CellData.FLAG_CURRENT_MATCH;

                    // URL hover
                    if (hoveredUrl is { } url &&
                        visRow == url.row && col >= url.startCol && col <= url.endCol)
                        flags |= CellData.FLAG_URL_HOVER;

                    // Cursor
                    uint cStyle = 0;
                    if (!isScrollback && bufferRow == buffer.CursorRow &&
                        col == buffer.CursorCol && cursorVisible)
                    {
                        flags |= CellData.FLAG_CURSOR;
                        cStyle = (cursorStyle ?? "bar").ToLowerInvariant() switch
                        {
                            "block"     => 1,
                            "bar"       => 2,
                            "underline" => 3,
                            _           => 2,
                        };
                    }

                    var cellData = new CellData
                    {
                        Foreground = new Vector4(fg.R / 255f, fg.G / 255f, fg.B / 255f,
                            cellFg.IsDefault ? 1f : 1f),
                        Background = new Vector4(bg.R / 255f, bg.G / 255f, bg.B / 255f,
                            cellBg.IsDefault ? 0f : (isInverse ? 1f : 0.63f)),
                        AtlasUV = new Vector4(glyphInfo.U0, glyphInfo.V0, glyphInfo.U1, glyphInfo.V1),
                        Flags = flags,
                        CursorStyle = cStyle,
                    };

                    _cellBuffer.SetCell(visRow, col, in cellData);
                }
            }

            buffer.ClearDirtyFlags();
        }

        // ── GPU rendering (no lock held) ────────────────────────────
        _cellBuffer.Upload(_context);

        // Update constant buffer
        // Cell dimensions and padding must be in device pixels (not WPF DIPs)
        // because the shader operates in pixel space matching the swap chain.
        float cellWidthPx = _cellWidth * _dpi;
        float cellHeightPx = _cellHeight * _dpi;
        float paddingPx = HorizontalPadding * _dpi;

        var constants = new ConstantBufferData
        {
            CellSize = new Vector2(cellWidthPx, cellHeightPx),
            GridSize = new Vector2(_cols, _rows),
            AtlasSize = new Vector2(AtlasSize, AtlasSize),
            ViewportSize = new Vector2(_pixelWidth, _pixelHeight),
            GridOffset = new Vector2(paddingPx, 0),
            CursorAlpha = cursorAlpha,
            BellAlpha = bellAlpha,
        };

        var mapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        try
        {
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        }
        finally
        {
            _context.Unmap(_constantBuffer!, 0);
        }

        // Set pipeline state
        _context.OMSetRenderTargets(_rtv);
        _context.RSSetViewport(0, 0, _pixelWidth, _pixelHeight);

        // Clear with background color
        var bgColor = _theme.Background;
        _context.ClearRenderTargetView(
            _rtv,
            new Color4(bgColor.R / 255f, bgColor.G / 255f, bgColor.B / 255f, 1f));

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null); // Vertices generated in shader

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.VSSetShaderResource(0, _cellBuffer.SRV);

        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _cellBuffer.SRV);
        _context.PSSetShaderResource(1, _atlas.AtlasSrv);
        _context.PSSetSampler(0, _sampler);

        // Single instanced draw call for the entire grid
        _context.DrawInstanced(6, (uint)_cellBuffer.CellCount, 0, 0);

        // Present with VSync
        var hr = _swapChain.Present(1, PresentFlags.None);
        if (hr == Vortice.DXGI.ResultCode.DeviceRemoved ||
            hr == Vortice.DXGI.ResultCode.DeviceReset)
        {
            HandleDeviceLost();
        }
    }

    // ── Resize / reconfigure ────────────────────────────────────────

    /// <summary>
    /// Resizes the swap chain and cell buffer after the host window changes size.
    /// </summary>
    public void ResizeSwapChain(
        int pixelWidth, int pixelHeight,
        int cols, int rows,
        float cellWidth, float cellHeight)
    {
        if (_disposed || _swapChain == null) return;

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _cols = cols;
        _rows = rows;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;

        _rtv?.Dispose();
        _rtv = null;

        _swapChain.ResizeBuffers(0, (uint)pixelWidth, (uint)pixelHeight, Format.Unknown, SwapChainFlags.None);
        CreateRenderTarget();

        _cellBuffer?.Resize(cols, rows);
    }

    /// <summary>Applies a new color theme (takes effect on the next Render call).</summary>
    public void UpdateTheme(GhosttyTheme theme) => _theme = theme;

    /// <summary>Recreates the glyph atlas after a font or DPI change.</summary>
    public void UpdateFont(string fontFamily, float fontSize, float dpi,
        float cellWidth, float cellHeight)
    {
        _fontSize = fontSize;
        _dpi = dpi;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;

        _atlas?.Invalidate(fontSize, dpi, fontFamily);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void CreateRenderTarget()
    {
        _rtv?.Dispose();
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private void HandleDeviceLost()
    {
        // Full teardown and recreation would happen here.
        // For now, log and let the next frame retry.
        Debug.WriteLine("[D3DTerminalRenderer] Device lost, needs recreation");
    }

    private static TerminalCell GetCell(
        int col, bool isScrollback, TerminalCell[]? scrollbackLine,
        int bufferRow, TerminalBuffer buffer)
    {
        if (isScrollback)
            return (scrollbackLine != null && col < scrollbackLine.Length)
                ? scrollbackLine[col]
                : TerminalCell.Empty;
        if (bufferRow >= 0 && bufferRow < buffer.Rows && col < buffer.Cols)
            return buffer.CellAt(bufferRow, col);
        return TerminalCell.Empty;
    }

    // ── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cellBuffer?.Dispose();
        _atlas?.Dispose();
        _sampler?.Dispose();
        _constantBuffer?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _rtv?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
