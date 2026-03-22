using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Cmux.Core.Models;
using Cmux.Core.Terminal;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cmux.Rendering;

/// <summary>
/// Main D3D11 rendering orchestrator: initializes the device, offscreen render target, and shader
/// pipeline, then drives per-frame cell population and instanced draw calls.
/// The rendered frame is copied to a CPU-readable staging texture for WriteableBitmap presentation.
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
        public float ScrollbarThumbTop;    // normalized 0..1 within viewport
        public float ScrollbarThumbHeight; // normalized 0..1
        public float ScrollbarAlpha;       // 0 = hidden, >0 = thumb opacity
        public float _scrollPad;           // padding to 16-byte alignment
    }

    // ── D3D11 / DXGI objects ────────────────────────────────────────
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _offscreenTarget;
    private ID3D11Texture2D? _stagingTexture;
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
    private float _cellWidth;   // WPF DIPs
    private float _cellHeight;  // WPF DIPs
    private int _cellWidthPx;   // device pixels (rounded, used for atlas + shader)
    private int _cellHeightPx;  // device pixels (rounded, used for atlas + shader)
    private float _fontSize;
    private float _dpi;
    private GhosttyTheme _theme = new();
    private bool _disposed;
    private const float HorizontalPadding = 20f;

    // Atlas texture is always 2048x2048 (matches GlyphAtlas internal constant).
    private const float AtlasSize = 2048f;

    /// <summary>Whether the renderer has been successfully initialized and is not disposed.</summary>
    public bool IsInitialized => _device != null && !_disposed;

    /// <summary>Current render target pixel dimensions (for mismatch detection).</summary>
    public int PixelWidth => _pixelWidth;
    public int PixelHeight => _pixelHeight;

    // ── Initialization ──────────────────────────────────────────────

    /// <summary>
    /// Creates the D3D11 device, offscreen render target, shaders, and GPU resources.
    /// </summary>
    public void Initialize(
        int pixelWidth, int pixelHeight,
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
        _cellWidthPx = (int)Math.Round(cellWidth * dpi);
        _cellHeightPx = (int)Math.Round(cellHeight * dpi);
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

        // 2. Create offscreen render target (replaces swap chain back buffer)
        var rtDesc = new Texture2DDescription
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _offscreenTarget = _device!.CreateTexture2D(rtDesc);
        _rtv = _device.CreateRenderTargetView(_offscreenTarget);

        // 3. Create staging texture for CPU readback
        var stagingDesc = rtDesc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingTexture = _device.CreateTexture2D(stagingDesc);

        // 4. Compile shaders (numbering continues from texture creation above)
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
        _atlas = new GlyphAtlas(_device, _context, fontFamily, fontSize, dpi,
            _cellWidthPx, _cellHeightPx);
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
            _offscreenTarget == null || _rtv == null || _atlas == null || _cellBuffer == null)
            return;

        // Recover from null RTV (CreateRenderTarget failed previously)
        if (_rtv == null)
        {
            try { CreateRenderTarget(); }
            catch
            {
                Debug.WriteLine("[D3DTerminalRenderer] RTV recovery failed — requesting full recreation");
                _disposed = true;
                return;
            }
        }

        var buffer = session.Buffer;

        // ── Unbind atlas SRV before cell population ─────────────────
        // GetOrRasterize() may call UpdateSubresource on the atlas texture.
        // If the atlas SRV is still bound from the previous frame's draw call,
        // this creates a D3D11 hazard (write to resource bound for reading)
        // that can trigger GPU driver bugs causing full-screen brightness flicker.
        _context.PSSetShaderResources(1, Array.Empty<ID3D11ShaderResourceView>());

        // ── Populate CellBuffer under render lock ───────────────────
        // scrollbackCount MUST be captured inside the lock to match the buffer state
        int renderScrollbackCount = 0;
        lock (session.RenderLock)
        {
            renderScrollbackCount = buffer.ScrollbackCount;
            int viewStartLine = renderScrollbackCount + scrollOffset;

            for (int visRow = 0; visRow < _rows; visRow++)
            {
                int virtualLine = viewStartLine + visRow;
                bool isScrollback = virtualLine < renderScrollbackCount;
                int bufferRow = virtualLine - renderScrollbackCount;

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
                        selection.IsSelected(visRow, col, scrollbackOffset, renderScrollbackCount))
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

                    // Cursor disabled in GPU renderer
                    uint cStyle = 0;

                    // Use theme background for default-bg cells so the terminal
                    // interior matches the theme color (not black).
                    var effectiveBg = cellBg.IsDefault ? _theme.Background : bg;
                    float bgAlpha = cellBg.IsDefault ? 1f : (isInverse ? 1f : 0.63f);

                    var cellData = new CellData
                    {
                        Foreground = new Vector4(fg.R / 255f, fg.G / 255f, fg.B / 255f, 1f),
                        Background = new Vector4(effectiveBg.R / 255f, effectiveBg.G / 255f, effectiveBg.B / 255f, bgAlpha),
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
        // Use the same integer pixel sizes as the atlas to prevent mismatches.
        float paddingPx = MathF.Round(HorizontalPadding * _dpi);

        // Scrollbar position (normalized 0..1)
        float sbThumbTop = 0f, sbThumbHeight = 0f, sbAlpha = 0f;
        int sbScrollback = renderScrollbackCount;
        if (sbScrollback > 0)
        {
            int sbTotal = sbScrollback + _rows;
            float thumbRatio = (float)_rows / sbTotal;
            sbThumbHeight = MathF.Max(20f / _pixelHeight, thumbRatio);
            int sbViewStart = sbScrollback + scrollOffset;
            float scrollFraction = (float)sbViewStart / MathF.Max(1, sbTotal - _rows);
            sbThumbTop = scrollFraction * (1f - sbThumbHeight);
            // Brighter when scrolled back, subtle when at bottom
            sbAlpha = scrollOffset < 0 ? 0.47f : 0.24f;
        }

        var constants = new ConstantBufferData
        {
            CellSize = new Vector2(_cellWidthPx, _cellHeightPx),
            GridSize = new Vector2(_cols, _rows),
            AtlasSize = new Vector2(AtlasSize, AtlasSize),
            ViewportSize = new Vector2(_pixelWidth, _pixelHeight),
            GridOffset = new Vector2(paddingPx, 0),
            CursorAlpha = cursorAlpha,
            BellAlpha = bellAlpha,
            ScrollbarThumbTop = sbThumbTop,
            ScrollbarThumbHeight = sbThumbHeight,
            ScrollbarAlpha = sbAlpha,
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
        _context.OMSetRenderTargets(_rtv!);
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

        // Frame is ready in _offscreenTarget — caller will call CopyFrameToBuffer()
    }

    // ── Frame readback ───────────────────────────────────────────────

    /// <summary>
    /// Copies the last rendered frame into the provided pixel buffer (BGRA, row-major).
    /// Returns true if the copy succeeded.
    /// </summary>
    public bool CopyFrameToBuffer(Span<byte> destination)
    {
        if (_disposed || _context == null || _offscreenTarget == null || _stagingTexture == null)
            return false;

        _context.CopyResource(_stagingTexture, _offscreenTarget);
        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try
        {
            unsafe
            {
                // Copy row-by-row (mapped pitch may differ from texture width)
                int rowBytes = _pixelWidth * 4;
                for (int y = 0; y < _pixelHeight; y++)
                {
                    var src = new ReadOnlySpan<byte>(
                        (byte*)mapped.DataPointer + y * mapped.RowPitch, rowBytes);
                    src.CopyTo(destination.Slice(y * rowBytes, rowBytes));
                }
            }
            return true;
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    // ── Resize / reconfigure ────────────────────────────────────────

    /// <summary>
    /// Resizes the offscreen render target and cell buffer after the control changes size.
    /// </summary>
    public void ResizeRenderTarget(
        int pixelWidth, int pixelHeight,
        int cols, int rows,
        float cellWidth, float cellHeight)
    {
        if (_disposed || _device == null) return;

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _cols = cols;
        _rows = rows;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _cellWidthPx = (int)Math.Round(cellWidth * _dpi);
        _cellHeightPx = (int)Math.Round(cellHeight * _dpi);

        _rtv?.Dispose();
        _rtv = null;
        _offscreenTarget?.Dispose();
        _stagingTexture?.Dispose();

        var rtDesc = new Texture2DDescription
        {
            Width = (uint)pixelWidth, Height = (uint)pixelHeight,
            MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _offscreenTarget = _device.CreateTexture2D(rtDesc);

        var stagingDesc = rtDesc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingTexture = _device.CreateTexture2D(stagingDesc);

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

        _cellWidthPx = (int)Math.Round(cellWidth * dpi);
        _cellHeightPx = (int)Math.Round(cellHeight * dpi);
        _atlas?.Invalidate(fontSize, dpi, fontFamily, _cellWidthPx, _cellHeightPx);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void CreateRenderTarget()
    {
        _rtv?.Dispose();
        _rtv = null; // Prevent use of disposed RTV if recreation fails
        _rtv = _device!.CreateRenderTargetView(_offscreenTarget!);
    }

    private void HandleDeviceLost()
    {
        // Recreate render target — most common recovery for device lost
        try
        {
            _rtv?.Dispose();
            _rtv = null;
            CreateRenderTarget();
            Debug.WriteLine("[D3DTerminalRenderer] Device lost — render target recreated");
        }
        catch
        {
            // Full device loss — mark as disposed so TerminalControl recreates us
            Debug.WriteLine("[D3DTerminalRenderer] Device lost — full recreation needed");
            _disposed = true;
        }
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
        _stagingTexture?.Dispose();
        _offscreenTarget?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
