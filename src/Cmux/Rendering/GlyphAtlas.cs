using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cmux.Rendering;

/// <summary>Glyph style variant for atlas lookup.</summary>
public enum GlyphStyle : byte
{
    Regular    = 0,
    Bold       = 1,
    Italic     = 2,
    BoldItalic = 3,
}

/// <summary>Cache key combining a Unicode codepoint with a style variant.</summary>
public readonly record struct GlyphKey(uint Codepoint, GlyphStyle Style);

/// <summary>UV coordinates and pixel dimensions of a rasterized glyph in the atlas texture.</summary>
public readonly record struct GlyphInfo(
    float U0,
    float V0,
    float U1,
    float V1,
    int   PixelWidth,
    int   PixelHeight,
    int   OffsetX,
    int   OffsetY);

/// <summary>
/// Rasterizes glyphs via DirectWrite ClearType into a BGRA GPU texture atlas.
/// Glyphs are packed row-by-row; the atlas is rebuilt from scratch when full.
/// Call <see cref="Invalidate"/> after font-size or DPI changes.
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    private const int AtlasWidth  = 2048;
    private const int AtlasHeight = 2048;
    private const int Padding     = 1; // pixel gap between glyphs

    // -------------------------------------------------------------------------
    // DirectWrite objects
    // -------------------------------------------------------------------------

    private IDWriteFactory     _dwriteFactory  = null!;
    private IDWriteFontFace[]  _fontFaces      = null!; // indexed by GlyphStyle

    // -------------------------------------------------------------------------
    // D3D11 objects
    // -------------------------------------------------------------------------

    private readonly ID3D11Device        _device;
    private readonly ID3D11DeviceContext _context;
    private          ID3D11Texture2D         _atlasTexture = null!;
    private          ID3D11ShaderResourceView _atlasSrv    = null!;

    // -------------------------------------------------------------------------
    // Packing state
    // -------------------------------------------------------------------------

    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

    // -------------------------------------------------------------------------
    // Glyph cache
    // -------------------------------------------------------------------------

    private readonly Dictionary<GlyphKey, GlyphInfo> _cache = new();

    // -------------------------------------------------------------------------
    // Runtime parameters
    // -------------------------------------------------------------------------

    private float  _fontSize;
    private float  _pixelsPerDip;
    private string _fontFamily;
    private int    _cellWidthPx;
    private int    _cellHeightPx;
    private float  _ascent; // baseline offset from cell top, in device pixels

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a new GlyphAtlas.
    /// </summary>
    /// <param name="device">The D3D11 device used to create the atlas texture.</param>
    /// <param name="context">The D3D11 device context used to upload glyph data.</param>
    /// <param name="fontFamily">Font family name (e.g. "Cascadia Code").</param>
    /// <param name="fontSize">Font size in DIPs.</param>
    /// <param name="pixelsPerDip">Screen DPI scale factor (e.g. 1.5 for 144 DPI).</param>
    public GlyphAtlas(
        ID3D11Device        device,
        ID3D11DeviceContext context,
        string              fontFamily   = "Cascadia Code",
        float               fontSize     = 14f,
        float               pixelsPerDip = 1f,
        int                 cellWidthPx  = 0,
        int                 cellHeightPx = 0)
    {
        _device       = device;
        _context      = context;
        _fontFamily   = fontFamily;
        _fontSize     = fontSize;
        _pixelsPerDip = pixelsPerDip;

        InitializeDirectWrite();

        // Compute cell pixel dimensions from font metrics if not provided
        if (cellWidthPx > 0 && cellHeightPx > 0)
        {
            _cellWidthPx = cellWidthPx;
            _cellHeightPx = cellHeightPx;
        }
        else
        {
            ComputeCellDimensions();
        }

        // Compute baseline (ascent) from font metrics
        var metrics = _fontFaces[0].Metrics;
        float designToPixel = _fontSize * _pixelsPerDip / metrics.DesignUnitsPerEm;
        _ascent = metrics.Ascent * designToPixel;

        CreateAtlasTexture();
    }

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>The GPU shader-resource view for the atlas texture (BGRA 2048x2048).</summary>
    public ID3D11ShaderResourceView AtlasSrv => _atlasSrv;

    /// <summary>
    /// Returns UV coordinates for <paramref name="codepoint"/> in <paramref name="style"/>,
    /// rasterizing it on demand if not already cached.
    /// </summary>
    public GlyphInfo GetOrRasterize(uint codepoint, GlyphStyle style)
    {
        var key = new GlyphKey(codepoint, style);
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        return RasterizeAndCache(key);
    }

    /// <summary>
    /// Invalidates the atlas after a font-size or DPI change.
    /// The next call to <see cref="GetOrRasterize"/> will rasterize into a fresh atlas.
    /// </summary>
    public void Invalidate(float? newFontSize = null, float? newPixelsPerDip = null, string? newFontFamily = null,
        int cellWidthPx = 0, int cellHeightPx = 0)
    {
        if (newFontSize    != null) _fontSize     = newFontSize.Value;
        if (newPixelsPerDip != null) _pixelsPerDip = newPixelsPerDip.Value;
        if (newFontFamily  != null && newFontFamily != _fontFamily)
        {
            _fontFamily = newFontFamily;
            DisposeFontFaces();
            ResolveFontFaces();
        }

        if (cellWidthPx > 0) _cellWidthPx = cellWidthPx;
        if (cellHeightPx > 0) _cellHeightPx = cellHeightPx;

        // Recompute ascent
        var metrics = _fontFaces[0].Metrics;
        float designToPixel = _fontSize * _pixelsPerDip / metrics.DesignUnitsPerEm;
        _ascent = metrics.Ascent * designToPixel;

        ClearAtlas();
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    private void ComputeCellDimensions()
    {
        var metrics = _fontFaces[0].Metrics;
        float designToPixel = _fontSize * _pixelsPerDip / metrics.DesignUnitsPerEm;
        _cellHeightPx = (int)Math.Ceiling((metrics.Ascent + metrics.Descent + metrics.LineGap) * designToPixel);
        // For monospace, use average advance width
        var glyphIndices = _fontFaces[0].GetGlyphIndices(new uint[] { 'M' });
        if (glyphIndices.Length > 0)
        {
            var glyphMetrics = _fontFaces[0].GetDesignGlyphMetrics(glyphIndices, false);
            if (glyphMetrics.Length > 0)
                _cellWidthPx = (int)Math.Ceiling(glyphMetrics[0].AdvanceWidth * designToPixel);
        }
        if (_cellWidthPx <= 0) _cellWidthPx = _cellHeightPx / 2; // fallback
    }

    private void InitializeDirectWrite()
    {
        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(FactoryType.Shared);
        ResolveFontFaces();
    }

    private void ResolveFontFaces()
    {
        _fontFaces = new IDWriteFontFace[4];

        using var collection = _dwriteFactory.GetSystemFontCollection(checkForUpdates: false);

        // Find the family index; fall back to "Consolas" then any available mono font.
        if (!TryFindFontFamily(collection, _fontFamily, out uint familyIndex))
        {
            if (!TryFindFontFamily(collection, "Consolas", out familyIndex))
                familyIndex = 0; // last resort: whatever is first
        }

        using var family = collection.GetFontFamily(familyIndex);

        _fontFaces[(int)GlyphStyle.Regular]    = CreateFontFace(family, FontWeight.Regular,  FontStyle.Normal);
        _fontFaces[(int)GlyphStyle.Bold]       = CreateFontFace(family, FontWeight.Bold,     FontStyle.Normal);
        _fontFaces[(int)GlyphStyle.Italic]     = CreateFontFace(family, FontWeight.Regular,  FontStyle.Italic);
        _fontFaces[(int)GlyphStyle.BoldItalic] = CreateFontFace(family, FontWeight.Bold,     FontStyle.Italic);
    }

    private static bool TryFindFontFamily(IDWriteFontCollection collection, string name, out uint index)
    {
        bool found = collection.FindFamilyName(name, out index);
        return found;
    }

    private static IDWriteFontFace CreateFontFace(IDWriteFontFamily family, FontWeight weight, FontStyle style)
    {
        using var font = family.GetFirstMatchingFont(weight, FontStretch.Normal, style);
        return font.CreateFontFace();
    }

    private void CreateAtlasTexture()
    {
        var desc = new Texture2DDescription
        {
            Width             = (uint)AtlasWidth,
            Height            = (uint)AtlasHeight,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.None,
            MiscFlags         = ResourceOptionFlags.None,
        };

        _atlasTexture = _device.CreateTexture2D(in desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format        = Format.B8G8R8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D     = new Texture2DShaderResourceView
            {
                MipLevels       = 1,
                MostDetailedMip = 0,
            },
        };

        _atlasSrv = _device.CreateShaderResourceView(_atlasTexture, srvDesc);
    }

    // -------------------------------------------------------------------------
    // Rasterization
    // -------------------------------------------------------------------------

    private GlyphInfo RasterizeAndCache(GlyphKey key)
    {
        var fontFace = _fontFaces[(int)key.Style];

        // Resolve glyph index for the codepoint.
        var codePoints   = new uint[] { key.Codepoint };
        var glyphIndices = fontFace.GetGlyphIndices(codePoints);

        if (glyphIndices.Length == 0 || glyphIndices[0] == 0)
        {
            // Unmapped codepoint — return a zero-sized entry so we don't retry.
            var empty = new GlyphInfo(0f, 0f, 0f, 0f, 0, 0, 0, 0);
            _cache[key] = empty;
            return empty;
        }

        // Build a GlyphRun with a single glyph at the origin.
        var glyphRun = new GlyphRun
        {
            FontFace    = fontFace,
            FontEmSize  = _fontSize,
            Indices     = glyphIndices,
            Advances    = new float[] { 0f }, // we only care about bounds
            Offsets     = null,
            IsSideways  = false,
            BidiLevel   = 0,
        };

        // Create analysis at the origin.
        using var analysis = _dwriteFactory.CreateGlyphRunAnalysis(
            glyphRun,
            _pixelsPerDip,
            transform:     null,
            renderingMode: RenderingMode.CleartypeNaturalSymmetric,
            measuringMode: MeasuringMode.Natural,
            baselineOriginX: 0f,
            baselineOriginY: 0f);

        var bounds = analysis.GetAlphaTextureBounds(TextureType.Cleartype3x1);

        int glyphW = bounds.Right  - bounds.Left;
        int glyphH = bounds.Bottom - bounds.Top;

        if (glyphW <= 0 || glyphH <= 0)
        {
            // Whitespace or zero-size glyph.
            var empty = new GlyphInfo(0f, 0f, 0f, 0f, 0, 0, 0, 0);
            _cache[key] = empty;
            return empty;
        }

        // Advance atlas cursor — use CELL-sized slots (not tight glyph bounds).
        // This ensures the shader's UV mapping (which covers the full cell quad)
        // doesn't stretch the glyph.
        if (_cursorX + _cellWidthPx + Padding > AtlasWidth)
        {
            _cursorX   = 0;
            _cursorY  += _rowHeight + Padding;
            _rowHeight  = 0;
        }

        if (_cursorY + _cellHeightPx + Padding > AtlasHeight)
        {
            ClearAtlas();
        }

        // Fetch ClearType alpha mask (3 bytes per pixel: R, G, B sub-pixel weights).
        int ctByteCount = glyphW * glyphH * 3;
        var ctBuffer    = new byte[ctByteCount];
        analysis.CreateAlphaTexture(TextureType.Cleartype3x1, bounds, ctBuffer, (uint)ctByteCount);

        // Convert to BGRA (4 bytes per pixel).
        var glyphBgra = ConvertCleartypeToBgra(ctBuffer, glyphW, glyphH);

        // Blit the glyph into a cell-sized BGRA buffer at the correct position.
        // The glyph is positioned relative to the baseline (ascent from cell top).
        int destX = Math.Max(0, bounds.Left);
        int destY = Math.Max(0, (int)_ascent + bounds.Top);
        var cellBgra = new byte[_cellWidthPx * _cellHeightPx * 4]; // zeroed = transparent

        for (int row = 0; row < glyphH; row++)
        {
            int cellRow = destY + row;
            if (cellRow < 0 || cellRow >= _cellHeightPx) continue;
            for (int col = 0; col < glyphW; col++)
            {
                int cellCol = destX + col;
                if (cellCol < 0 || cellCol >= _cellWidthPx) continue;
                int srcIdx = (row * glyphW + col) * 4;
                int dstIdx = (cellRow * _cellWidthPx + cellCol) * 4;
                cellBgra[dstIdx]     = glyphBgra[srcIdx];
                cellBgra[dstIdx + 1] = glyphBgra[srcIdx + 1];
                cellBgra[dstIdx + 2] = glyphBgra[srcIdx + 2];
                cellBgra[dstIdx + 3] = glyphBgra[srcIdx + 3];
            }
        }

        // Upload cell-sized buffer to atlas.
        UploadGlyphToAtlas(cellBgra, _cellWidthPx, _cellHeightPx, _cursorX, _cursorY);

        // UV covers the full cell slot.
        float u0 = _cursorX                    / (float)AtlasWidth;
        float v0 = _cursorY                    / (float)AtlasHeight;
        float u1 = (_cursorX + _cellWidthPx)   / (float)AtlasWidth;
        float v1 = (_cursorY + _cellHeightPx)  / (float)AtlasHeight;

        var info = new GlyphInfo(u0, v0, u1, v1, _cellWidthPx, _cellHeightPx, bounds.Left, bounds.Top);
        _cache[key] = info;

        // Advance cursor by cell size.
        _cursorX  += _cellWidthPx + Padding;
        if (_cellHeightPx > _rowHeight) _rowHeight = _cellHeightPx;

        return info;
    }

    /// <summary>
    /// Converts a ClearType 3-byte-per-pixel buffer (R,G,B sub-pixel weights) into
    /// a BGRA 4-byte-per-pixel buffer. The alpha channel is set to 0xFF so the GPU
    /// shader can multiply it by the foreground colour using sub-pixel blending.
    /// </summary>
    private static byte[] ConvertCleartypeToBgra(byte[] ct, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIdx  = (y * width + x) * 3;
                int dstIdx  = (y * width + x) * 4;
                byte r = ct[srcIdx];
                byte g = ct[srcIdx + 1];
                byte b = ct[srcIdx + 2];
                // BGRA layout expected by DXGI_FORMAT_B8G8R8A8_UNORM.
                bgra[dstIdx]     = b;
                bgra[dstIdx + 1] = g;
                bgra[dstIdx + 2] = r;
                bgra[dstIdx + 3] = 0xFF;
            }
        }
        return bgra;
    }

    private unsafe void UploadGlyphToAtlas(byte[] bgra, int glyphW, int glyphH, int destX, int destY)
    {
        uint rowPitch = (uint)(glyphW * 4);

        var region = new Box(
            left:   destX,
            top:    destY,
            front:  0,
            right:  destX + glyphW,
            bottom: destY + glyphH,
            back:   1);

        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = handle.AddrOfPinnedObject();
            _context.UpdateSubresource(_atlasTexture, 0, region, ptr, rowPitch, 0);
        }
        finally
        {
            handle.Free();
        }
    }

    // -------------------------------------------------------------------------
    // Atlas management
    // -------------------------------------------------------------------------

    private void ClearAtlas()
    {
        _cache.Clear();
        _cursorX   = 0;
        _cursorY   = 0;
        _rowHeight  = 0;

        // Clear the texture by uploading a zeroed buffer.
        int   totalPixels = AtlasWidth * AtlasHeight * 4;
        var   zeros       = new byte[totalPixels];
        GCHandle handle   = GCHandle.Alloc(zeros, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(
                _atlasTexture,
                0,
                (Box?)null,
                handle.AddrOfPinnedObject(),
                (uint)(AtlasWidth * 4),
                0);
        }
        finally
        {
            handle.Free();
        }
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    private void DisposeFontFaces()
    {
        if (_fontFaces == null) return;
        foreach (var face in _fontFaces)
            face?.Dispose();
    }

    public void Dispose()
    {
        DisposeFontFaces();
        _atlasSrv?.Dispose();
        _atlasTexture?.Dispose();
        _dwriteFactory?.Dispose();
    }
}
