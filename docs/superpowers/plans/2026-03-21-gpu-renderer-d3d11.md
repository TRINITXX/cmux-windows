# GPU Terminal Renderer (D3D11) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WPF DrawingVisual renderer with a Direct3D 11 GPU pipeline using Vortice.Windows + DirectWrite for WezTerm-level fluidity.

**Architecture:** HwndHost embeds a D3D11 SwapChain inside the WPF TerminalControl. A GlyphAtlas rasterizes glyphs via DirectWrite ClearType into a GPU texture. A CellBuffer uploads per-cell data (colors, atlas UVs, flags) to the GPU. A single instanced draw call renders the entire terminal grid via an HLSL shader.

**Tech Stack:** .NET 10, WPF, Vortice.Direct3D11, Vortice.DirectWrite, Vortice.DXGI, Vortice.D3DCompiler, HLSL

**Spec:** `docs/superpowers/specs/2026-03-21-gpu-renderer-d3d11-design.md`

---

## File Structure

```
src/Cmux/
  Controls/
    TerminalControl.cs           # MODIFY: strip rendering, delegate to D3DTerminalRenderer
    D3DRenderHost.cs             # CREATE: HwndHost subclass for D3D11 HWND
  Rendering/
    D3DTerminalRenderer.cs       # CREATE: D3D11 pipeline orchestration (~400 lines)
    GlyphAtlas.cs                # CREATE: DirectWrite rasterization + atlas texture (~350 lines)
    CellBuffer.cs                # CREATE: CPU cell array + GPU structured buffer (~200 lines)
    ShaderLoader.cs              # CREATE: load pre-compiled .cso or runtime compile (~80 lines)
  Shaders/
    TerminalShader.hlsl          # CREATE: vertex + pixel shader (~200 lines)
  Cmux.csproj                   # MODIFY: add Vortice NuGet packages
src/Cmux.Core/
  Terminal/TerminalBuffer.cs     # NO CHANGE (dirty tracking API preserved as-is)
  Terminal/TerminalAttribute.cs  # NO CHANGE
```

---

## Task 1: Add Vortice NuGet Dependencies

**Files:**

- Modify: `src/Cmux/Cmux.csproj`

- [ ] **Step 1: Add Vortice packages**

Add to `<ItemGroup>` in `src/Cmux/Cmux.csproj`:

```xml
<PackageReference Include="Vortice.Direct3D11" Version="3.7.0" />
<PackageReference Include="Vortice.DirectWrite" Version="3.7.0" />
<PackageReference Include="Vortice.DXGI" Version="3.7.0" />
<PackageReference Include="Vortice.D3DCompiler" Version="3.7.0" />
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds with new packages restored.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Cmux.csproj
git commit -m "chore(deps): add Vortice.Direct3D11/DirectWrite/DXGI packages"
```

---

## Task 2: Create HLSL Shader

**Files:**

- Create: `src/Cmux/Shaders/TerminalShader.hlsl`

This is the core GPU shader that renders the entire terminal grid in a single draw call.

- [ ] **Step 1: Create the shader file**

Create `src/Cmux/Shaders/TerminalShader.hlsl`:

```hlsl
// Terminal GPU Renderer — Vertex + Pixel Shader
// Renders the entire terminal grid in a single instanced draw call.
// Each instance = 1 cell (6 vertices = 2 triangles = 1 quad).

// --- Constant Buffer ---
cbuffer TerminalConstants : register(b0)
{
    float2 cellSize;      // cell dimensions in pixels
    float2 gridSize;      // (cols, rows)
    float2 atlasSize;     // atlas texture dimensions in pixels
    float2 viewportSize;  // render target dimensions in pixels
    float2 gridOffset;    // (left padding, top padding) in pixels
    float  cursorAlpha;   // 0..1 for blink animation
    float  bellAlpha;     // 0..1 for visual bell flash
};

// --- Cell Data (StructuredBuffer) ---
struct CellData
{
    float4 foreground;
    float4 background;
    float4 atlasUV;       // (u0, v0, u1, v1) normalized
    uint   flags;
    uint   cursorStyle;   // 0=none, 1=block, 2=bar, 3=underline
    uint   _pad0;
    uint   _pad1;
};

StructuredBuffer<CellData> cells : register(t0);
Texture2D    glyphAtlas : register(t1);
SamplerState glyphSampler : register(s0);

// --- Flag bits ---
static const uint FLAG_CURSOR        = 0x001;
static const uint FLAG_SELECTED      = 0x002;
static const uint FLAG_SEARCH_MATCH  = 0x004;
static const uint FLAG_CURRENT_MATCH = 0x008;
static const uint FLAG_UNDERLINE     = 0x010;
static const uint FLAG_STRIKETHROUGH = 0x020;
static const uint FLAG_DIM           = 0x040;
static const uint FLAG_URL_HOVER     = 0x080;
static const uint FLAG_WIDE          = 0x100;
static const uint FLAG_WIDE_PLACEHOLDER = 0x200;

// --- Vertex Shader Output ---
struct VS_OUTPUT
{
    float4 position : SV_Position;
    float2 texCoord : TEXCOORD0;
    float4 fgColor  : COLOR0;
    float4 bgColor  : COLOR1;
    uint   flags    : BLENDINDICES0;
    uint   cursorStyle : BLENDINDICES1;
    float2 cellUV   : TEXCOORD1;  // 0..1 within the cell (for decorations)
};

// --- Vertex Shader ---
VS_OUTPUT VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    VS_OUTPUT output;

    CellData cell = cells[instanceID];

    // Skip wide placeholders (degenerate quad)
    if (cell.flags & FLAG_WIDE_PLACEHOLDER)
    {
        output.position = float4(0, 0, 0, 0);
        output.texCoord = float2(0, 0);
        output.fgColor = float4(0, 0, 0, 0);
        output.bgColor = float4(0, 0, 0, 0);
        output.flags = 0;
        output.cursorStyle = 0;
        output.cellUV = float2(0, 0);
        return output;
    }

    uint cols = (uint)gridSize.x;
    uint col = instanceID % cols;
    uint row = instanceID / cols;

    float cellW = (cell.flags & FLAG_WIDE) ? cellSize.x * 2.0 : cellSize.x;
    float cellH = cellSize.y;

    // Quad corners (2 triangles, 6 vertices)
    // 0--1    Triangles: 0-1-2, 2-1-3
    // |  |
    // 2--3
    static const float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1)
    };

    float2 corner = corners[vertexID];

    // Pixel position
    float2 pixelPos = gridOffset + float2(col * cellSize.x, row * cellSize.y) + corner * float2(cellW, cellH);

    // Convert to NDC (-1..1, Y flipped)
    output.position = float4(
        (pixelPos.x / viewportSize.x) * 2.0 - 1.0,
        1.0 - (pixelPos.y / viewportSize.y) * 2.0,
        0.0, 1.0
    );

    // Atlas UV interpolation
    float2 uvMin = cell.atlasUV.xy;
    float2 uvMax = cell.atlasUV.zw;
    output.texCoord = lerp(uvMin, uvMax, corner);

    output.fgColor = cell.foreground;
    output.bgColor = cell.background;
    output.flags = cell.flags;
    output.cursorStyle = cell.cursorStyle;
    output.cellUV = corner;

    return output;
}

// --- Gamma helpers (sRGB linearize / re-gamma) ---
float3 SRGBToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float3 LinearToSRGB(float3 c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

// --- Pixel Shader ---
float4 PSMain(VS_OUTPUT input) : SV_Target
{
    float3 bg = input.bgColor.rgb;
    float3 fg = input.fgColor.rgb;
    uint flags = input.flags;

    // Start with background
    float3 color = bg;
    float alpha = input.bgColor.a;

    // ClearType glyph blending
    bool hasGlyph = any(input.texCoord > 0.001);
    if (hasGlyph && (input.fgColor.a > 0))
    {
        float4 atlasVal = glyphAtlas.Sample(glyphSampler, input.texCoord);

        // ClearType: R,G,B are per-subpixel coverage values
        float3 coverage = atlasVal.rgb;

        if (flags & FLAG_DIM)
            coverage *= 0.5;

        // Gamma-correct blending (linearize -> blend -> re-gamma)
        float3 bgLinear = SRGBToLinear(bg);
        float3 fgLinear = SRGBToLinear(fg);
        float3 blended = lerp(bgLinear, fgLinear, coverage);
        color = LinearToSRGB(blended);
        alpha = max(alpha, max(coverage.r, max(coverage.g, coverage.b)));
    }

    // Cursor overlay
    if ((flags & FLAG_CURSOR) && cursorAlpha > 0)
    {
        float cursorMask = 0;
        uint style = input.cursorStyle;

        if (style == 1) // block
            cursorMask = 1.0;
        else if (style == 2) // bar
            cursorMask = (input.cellUV.x < (2.0 / cellSize.x)) ? 1.0 : 0.0;
        else if (style == 3) // underline
            cursorMask = (input.cellUV.y > (1.0 - 2.0 / cellSize.y)) ? 1.0 : 0.0;

        if (cursorMask > 0)
        {
            float3 cursorColor = fg; // cursor uses foreground color
            color = lerp(color, cursorColor, cursorMask * cursorAlpha * 0.8);
        }
    }

    // Selection overlay
    if (flags & FLAG_SELECTED)
        color = lerp(color, float3(0.506, 0.549, 0.973), 0.4); // indigo-ish

    // Search highlights
    if (flags & FLAG_CURRENT_MATCH)
        color = lerp(color, float3(0.984, 0.573, 0.235), 0.7); // orange
    else if (flags & FLAG_SEARCH_MATCH)
        color = lerp(color, float3(0.984, 0.749, 0.141), 0.4); // yellow

    // Underline decoration (1px line at bottom)
    if (flags & FLAG_UNDERLINE)
    {
        if (input.cellUV.y > (1.0 - 1.5 / cellSize.y))
            color = fg;
    }

    // Strikethrough decoration (1px line at middle)
    if (flags & FLAG_STRIKETHROUGH)
    {
        float mid = 0.5;
        if (abs(input.cellUV.y - mid) < (0.75 / cellSize.y))
            color = fg;
    }

    // URL hover underline
    if (flags & FLAG_URL_HOVER)
    {
        if (input.cellUV.y > (1.0 - 1.5 / cellSize.y))
            color = float3(0.506, 0.549, 0.973); // accent color
    }

    // Visual bell flash
    if (bellAlpha > 0)
        color = lerp(color, float3(1, 1, 1), bellAlpha * 0.3);

    return float4(color, alpha);
}
```

- [ ] **Step 2: Add as embedded resource in csproj**

Add to `src/Cmux/Cmux.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Shaders\TerminalShader.hlsl" />
</ItemGroup>
```

- [ ] **Step 3: Build to verify no project errors**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds (HLSL is just an embedded resource at this point).

- [ ] **Step 4: Commit**

```bash
git add src/Cmux/Shaders/TerminalShader.hlsl src/Cmux/Cmux.csproj
git commit -m "feat(rendering): add HLSL terminal shader with ClearType blending"
```

---

## Task 3: Create ShaderLoader

**Files:**

- Create: `src/Cmux/Rendering/ShaderLoader.cs`

Loads pre-compiled `.cso` shaders from embedded resources, falls back to runtime HLSL compilation.

- [ ] **Step 1: Create ShaderLoader**

Create `src/Cmux/Rendering/ShaderLoader.cs`:

```csharp
using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Cmux.Rendering;

/// <summary>
/// Loads compiled HLSL shaders from embedded resources or compiles at runtime as fallback.
/// </summary>
internal static class ShaderLoader
{
    public static ID3D11VertexShader CreateVertexShader(ID3D11Device device, out Blob bytecode)
    {
        bytecode = CompileShader("VSMain", "vs_5_0");
        return device.CreateVertexShader(bytecode.AsBytes());
    }

    public static ID3D11PixelShader CreatePixelShader(ID3D11Device device)
    {
        using var bytecode = CompileShader("PSMain", "ps_5_0");
        return device.CreatePixelShader(bytecode.AsBytes());
    }

    private static Blob CompileShader(string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("TerminalShader.hlsl", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new InvalidOperationException("TerminalShader.hlsl embedded resource not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new System.IO.StreamReader(stream);
        var hlslSource = reader.ReadToEnd();

        var result = Compiler.Compile(
            hlslSource,
            entryPoint,
            "TerminalShader.hlsl",
            profile,
            ShaderFlags.OptimizationLevel3);

        if (result.Failed)
        {
            var errorMsg = result.GetError() ?? "Unknown shader compilation error";
            throw new InvalidOperationException($"HLSL compilation failed ({entryPoint}): {errorMsg}");
        }

        return result.GetResult();
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Rendering/ShaderLoader.cs
git commit -m "feat(rendering): add ShaderLoader for HLSL compile/load"
```

---

## Task 4: Create GlyphAtlas

**Files:**

- Create: `src/Cmux/Rendering/GlyphAtlas.cs`

Rasterizes glyphs via DirectWrite ClearType into a GPU texture atlas.

- [ ] **Step 1: Create GlyphAtlas**

Create `src/Cmux/Rendering/GlyphAtlas.cs`:

```csharp
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Cmux.Rendering;

/// <summary>
/// Manages a GPU texture atlas of rasterized glyphs using DirectWrite ClearType.
/// Glyphs are rasterized on-demand and cached by (codepoint, style).
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    public enum GlyphStyle : byte { Regular, Bold, Italic, BoldItalic }

    private readonly record struct GlyphKey(uint Codepoint, GlyphStyle Style);

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1, int PixelWidth, int PixelHeight);

    private readonly ID3D11Device _device;
    private readonly IDWriteFactory _dwriteFactory;
    private readonly IDWriteFontFace[] _fontFaces = new IDWriteFontFace[4]; // indexed by GlyphStyle
    private readonly float _fontSizeEm;
    private readonly float _dpi;

    private ID3D11Texture2D _atlasTexture;
    private ID3D11ShaderResourceView _atlasSrv;
    private readonly int _atlasWidth;
    private readonly int _atlasHeight;

    // Packing state (row-based)
    private int _packX;
    private int _packY;
    private int _packRowHeight;

    // Cell dimensions in pixels (for fixed-size slot packing)
    private readonly int _cellPixelWidth;
    private readonly int _cellPixelHeight;

    private readonly Dictionary<GlyphKey, GlyphInfo> _cache = new();

    // Empty glyph UV for cells with no character
    public static readonly GlyphInfo EmptyGlyph = new(0, 0, 0, 0, 0, 0);

    public ID3D11ShaderResourceView AtlasSRV => _atlasSrv;
    public int AtlasWidth => _atlasWidth;
    public int AtlasHeight => _atlasHeight;

    public GlyphAtlas(ID3D11Device device, string fontFamily, float fontSizeEm, float dpi,
        int cellPixelWidth, int cellPixelHeight)
    {
        _device = device;
        _fontSizeEm = fontSizeEm;
        _dpi = dpi;
        _cellPixelWidth = cellPixelWidth;
        _cellPixelHeight = cellPixelHeight;
        _atlasWidth = 2048;
        _atlasHeight = 2048;

        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        InitFontFaces(fontFamily);
        CreateAtlasTexture();
    }

    private void InitFontFaces(string fontFamily)
    {
        using var collection = _dwriteFactory.GetSystemFontCollection(false);

        if (!collection.FindFamilyName(fontFamily, out int familyIndex))
        {
            // Fallback to Consolas
            collection.FindFamilyName("Consolas", out familyIndex);
        }

        using var family = collection.GetFontFamily(familyIndex);

        var styles = new (FontWeight weight, FontStyle style)[]
        {
            (FontWeight.Normal, FontStyle.Normal),   // Regular
            (FontWeight.Bold, FontStyle.Normal),     // Bold
            (FontWeight.Normal, FontStyle.Italic),   // Italic
            (FontWeight.Bold, FontStyle.Italic),     // BoldItalic
        };

        for (int i = 0; i < 4; i++)
        {
            using var font = family.GetFirstMatchingFont(styles[i].weight, FontStretch.Normal, styles[i].style);
            _fontFaces[i] = font.CreateFontFace();
        }
    }

    private void CreateAtlasTexture()
    {
        _atlasTexture?.Dispose();
        _atlasSrv?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = _atlasWidth,
            Height = _atlasHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        _atlasTexture = _device.CreateTexture2D(desc);
        _atlasSrv = _device.CreateShaderResourceView(_atlasTexture);
    }

    /// <summary>
    /// Gets the atlas UV info for a glyph. Rasterizes on-demand if not cached.
    /// </summary>
    public GlyphInfo GetGlyph(uint codepoint, GlyphStyle style)
    {
        var key = new GlyphKey(codepoint, style);
        if (_cache.TryGetValue(key, out var info))
            return info;

        info = RasterizeGlyph(codepoint, style);
        _cache[key] = info;
        return info;
    }

    private GlyphInfo RasterizeGlyph(uint codepoint, GlyphStyle style)
    {
        var fontFace = _fontFaces[(int)style];

        // Get glyph index
        var codepoints = new uint[] { codepoint };
        var glyphIndices = new ushort[1];
        fontFace.GetGlyphIndices(codepoints, glyphIndices);

        if (glyphIndices[0] == 0)
            return EmptyGlyph; // Glyph not in font

        // Create glyph run
        var glyphRun = new GlyphRun
        {
            FontFace = fontFace,
            FontEmSize = _fontSizeEm * (_dpi / 72.0f), // Convert pt to device units
            GlyphCount = 1,
            GlyphIndices = glyphIndices,
            GlyphAdvances = new float[] { _cellPixelWidth },
            BidiLevel = 0,
            IsSideways = false,
        };

        // Create analysis for ClearType rendering
        using var analysis = _dwriteFactory.CreateGlyphRunAnalysis(
            glyphRun,
            1.0f, // pixelsPerDip (already accounted for in fontEmSize)
            null, // transform
            Vortice.DirectWrite.RenderingMode.CleartypeNatural,
            MeasuringMode.Natural,
            0, 0); // baseline origin

        // Get texture bounds
        var bounds = analysis.GetAlphaTextureBounds(TextureType.Cleartype3x1);

        int glyphW = bounds.Right - bounds.Left;
        int glyphH = bounds.Bottom - bounds.Top;

        if (glyphW <= 0 || glyphH <= 0)
            return EmptyGlyph;

        // Check if we need to advance to next row
        if (_packX + glyphW > _atlasWidth)
        {
            _packX = 0;
            _packY += _packRowHeight + 1; // 1px padding
            _packRowHeight = 0;
        }

        // Check if atlas is full
        if (_packY + glyphH > _atlasHeight)
        {
            RebuildAtlas();
            return RasterizeGlyph(codepoint, style); // Retry after rebuild
        }

        // Get ClearType alpha texture (3 bytes per pixel: R, G, B coverage)
        var alphaData = analysis.CreateAlphaTexture(TextureType.Cleartype3x1, bounds);

        // Convert to BGRA (4 bytes per pixel)
        var bgraData = new byte[glyphW * glyphH * 4];
        for (int i = 0; i < glyphW * glyphH; i++)
        {
            bgraData[i * 4 + 0] = alphaData[i * 3 + 2]; // B = blue coverage
            bgraData[i * 4 + 1] = alphaData[i * 3 + 1]; // G = green coverage
            bgraData[i * 4 + 2] = alphaData[i * 3 + 0]; // R = red coverage
            bgraData[i * 4 + 3] = 255;                    // A = opaque
        }

        // Upload to atlas
        var region = new ResourceRegion(_packX, _packY, 0, _packX + glyphW, _packY + glyphH, 1);
        var handle = GCHandle.Alloc(bgraData, GCHandleType.Pinned);
        try
        {
            _device.ImmediateContext.UpdateSubresource(
                _atlasTexture, 0, region,
                handle.AddrOfPinnedObject(),
                glyphW * 4, 0);
        }
        finally
        {
            handle.Free();
        }

        // Compute UV coordinates
        float u0 = (float)_packX / _atlasWidth;
        float v0 = (float)_packY / _atlasHeight;
        float u1 = (float)(_packX + glyphW) / _atlasWidth;
        float v1 = (float)(_packY + glyphH) / _atlasHeight;

        // Advance packing position
        _packX += glyphW + 1; // 1px padding
        _packRowHeight = Math.Max(_packRowHeight, glyphH);

        return new GlyphInfo(u0, v0, u1, v1, glyphW, glyphH);
    }

    private void RebuildAtlas()
    {
        // Clear all state and recreate texture
        _cache.Clear();
        _packX = 0;
        _packY = 0;
        _packRowHeight = 0;
        CreateAtlasTexture();
        // Glyphs will be re-rasterized on demand by the next Render() call
    }

    /// <summary>
    /// Invalidates the entire atlas. Call when font size or DPI changes.
    /// </summary>
    public void Invalidate()
    {
        RebuildAtlas();
    }

    public void Dispose()
    {
        _atlasSrv?.Dispose();
        _atlasTexture?.Dispose();
        foreach (var face in _fontFaces)
            face?.Dispose();
        _dwriteFactory?.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Rendering/GlyphAtlas.cs
git commit -m "feat(rendering): add GlyphAtlas with DirectWrite ClearType rasterization"
```

---

## Task 5: Create CellBuffer

**Files:**

- Create: `src/Cmux/Rendering/CellBuffer.cs`

CPU-side cell data array that maps the visible terminal viewport to GPU structured buffer.

- [ ] **Step 1: Create CellBuffer**

Create `src/Cmux/Rendering/CellBuffer.cs`:

```csharp
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Cmux.Core.Terminal;

namespace Cmux.Rendering;

/// <summary>
/// CPU-side cell data array representing the visible viewport.
/// Uploaded to GPU as a StructuredBuffer for the terminal shader.
/// </summary>
internal sealed class CellBuffer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct CellData
    {
        public Color4 Foreground;  // 16 bytes
        public Color4 Background;  // 16 bytes
        public Vector4 AtlasUV;   // 16 bytes (u0, v0, u1, v1)
        public uint Flags;         // 4 bytes
        public uint CursorStyle;   // 4 bytes
        public uint _pad0;         // 4 bytes
        public uint _pad1;         // 4 bytes
    }                              // Total: 64 bytes

    // Flag constants (must match HLSL)
    public const uint FLAG_CURSOR        = 0x001;
    public const uint FLAG_SELECTED      = 0x002;
    public const uint FLAG_SEARCH_MATCH  = 0x004;
    public const uint FLAG_CURRENT_MATCH = 0x008;
    public const uint FLAG_UNDERLINE     = 0x010;
    public const uint FLAG_STRIKETHROUGH = 0x020;
    public const uint FLAG_DIM           = 0x040;
    public const uint FLAG_URL_HOVER     = 0x080;
    public const uint FLAG_WIDE          = 0x100;
    public const uint FLAG_WIDE_PLACEHOLDER = 0x200;

    private readonly ID3D11Device _device;
    private CellData[] _cpuBuffer;
    private ID3D11Buffer _gpuBuffer;
    private ID3D11ShaderResourceView _srv;
    private int _cols;
    private int _rows;

    public ID3D11ShaderResourceView SRV => _srv;
    public int CellCount => _cols * _rows;

    public CellBuffer(ID3D11Device device, int cols, int rows)
    {
        _device = device;
        Resize(cols, rows);
    }

    public void Resize(int cols, int rows)
    {
        if (cols == _cols && rows == _rows && _gpuBuffer != null)
            return;

        _cols = cols;
        _rows = rows;
        _cpuBuffer = new CellData[cols * rows];

        _srv?.Dispose();
        _gpuBuffer?.Dispose();

        var desc = new BufferDescription
        {
            ByteWidth = cols * rows * Marshal.SizeOf<CellData>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = Marshal.SizeOf<CellData>(),
        };

        _gpuBuffer = _device.CreateBuffer(desc);
        _srv = _device.CreateShaderResourceView(_gpuBuffer,
            new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = cols * rows }
            });
    }

    /// <summary>
    /// Write cell data at (row, col) in the CPU buffer.
    /// </summary>
    public void SetCell(int row, int col, in CellData data)
    {
        _cpuBuffer[row * _cols + col] = data;
    }

    /// <summary>
    /// Upload the entire CPU buffer to the GPU.
    /// </summary>
    public void Upload(ID3D11DeviceContext context)
    {
        var mapped = context.Map(_gpuBuffer, MapMode.WriteDiscard);
        var span = new Span<CellData>(_cpuBuffer);
        MemoryMarshal.AsBytes(span).CopyTo(
            new Span<byte>((void*)mapped.DataPointer, _cpuBuffer.Length * Marshal.SizeOf<CellData>()));
        context.Unmap(_gpuBuffer, 0);
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _gpuBuffer?.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Rendering/CellBuffer.cs
git commit -m "feat(rendering): add CellBuffer for CPU-to-GPU cell data upload"
```

---

## Task 6: Create D3DRenderHost (HwndHost)

**Files:**

- Create: `src/Cmux/Controls/D3DRenderHost.cs`

WPF HwndHost that creates a child HWND for the D3D11 swap chain.

- [ ] **Step 1: Create D3DRenderHost**

Create `src/Cmux/Controls/D3DRenderHost.cs`:

```csharp
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Controls/D3DRenderHost.cs
git commit -m "feat(rendering): add D3DRenderHost HwndHost for D3D11 swap chain"
```

---

## Task 7: Create D3DTerminalRenderer

**Files:**

- Create: `src/Cmux/Rendering/D3DTerminalRenderer.cs`

The main orchestrator that initializes D3D11, manages the render pipeline, and draws each frame.

- [ ] **Step 1: Create D3DTerminalRenderer**

Create `src/Cmux/Rendering/D3DTerminalRenderer.cs`:

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Cmux.Core.Config;
using Cmux.Core.Terminal;

namespace Cmux.Rendering;

/// <summary>
/// Direct3D 11 renderer for the terminal grid.
/// Orchestrates GlyphAtlas, CellBuffer, and HLSL shader pipeline.
/// </summary>
internal sealed class D3DTerminalRenderer : IDisposable
{
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

    private ID3D11Device _device;
    private ID3D11DeviceContext _context;
    private IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView _rtv;
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11Buffer _constantBuffer;
    private ID3D11SamplerState _sampler;

    private GlyphAtlas _atlas;
    private CellBuffer _cellBuffer;

    private int _pixelWidth;
    private int _pixelHeight;
    private int _cols;
    private int _rows;
    private float _cellWidth;
    private float _cellHeight;
    private float _fontSize;
    private float _dpi;
    private GhosttyTheme _theme;
    private bool _disposed;

    private const float HorizontalPadding = 20f;

    public bool IsInitialized => _device != null;

    public void Initialize(nint hwnd, int pixelWidth, int pixelHeight,
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

        // Create D3D11 device
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out _device,
            out _context).CheckError();

        // Create swap chain
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var scDesc = new SwapChainDescription1
        {
            Width = pixelWidth,
            Height = pixelHeight,
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

        // Compile shaders
        using var vsBytecode = ShaderLoader.CreateVertexShader(_device, out var vsBlob);
        _vertexShader = _device.CreateVertexShader(vsBlob.AsBytes());
        _pixelShader = ShaderLoader.CreatePixelShader(_device);
        vsBlob.Dispose();

        // Constant buffer
        _constantBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (Marshal.SizeOf<ConstantBufferData>() + 15) & ~15, // 16-byte aligned
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // Sampler (point sampling for pixel-perfect glyph rendering)
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        });

        // Create atlas and cell buffer
        _atlas = new GlyphAtlas(_device, fontFamily, fontSize, dpi,
            (int)Math.Ceiling(cellWidth), (int)Math.Ceiling(cellHeight));
        _cellBuffer = new CellBuffer(_device, cols, rows);
    }

    private void CreateRenderTarget()
    {
        _rtv?.Dispose();
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>
    /// Populates the CellBuffer from the terminal buffer's visible viewport,
    /// then renders the grid via the GPU pipeline.
    /// </summary>
    public void Render(TerminalSession session, int scrollOffset, int viewRows,
        float cursorAlpha, float bellAlpha,
        TerminalSelection selection, int scrollbackOffset,
        HashSet<(int row, int col)>? searchMatchSet,
        HashSet<(int row, int col)>? currentMatchSet,
        (int row, int startCol, int endCol, string url)? hoveredUrl,
        string cursorStyle, bool cursorVisible)
    {
        if (_disposed || _device == null) return;

        var buffer = session.Buffer;
        int scrollbackCount = buffer.ScrollbackCount;
        int viewStartLine = scrollbackCount + scrollOffset;

        // --- Populate CellBuffer under RenderLock ---
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

                    // Resolve colors (same logic as current RenderRow)
                    var cellFg = isInverse
                        ? (attr.Background.IsDefault ? _theme.Background : attr.Background)
                        : attr.Foreground;
                    var cellBg = isInverse
                        ? (attr.Foreground.IsDefault ? _theme.Foreground : attr.Foreground)
                        : attr.Background;

                    var fg = cellFg.IsDefault ? _theme.Foreground : cellFg;
                    var bg = cellBg;

                    // Atlas lookup
                    var glyphInfo = GlyphAtlas.EmptyGlyph;
                    bool hasChar = cell.Character != '\0' && cell.Character != ' ';
                    if (hasChar)
                    {
                        var style = GlyphAtlas.GlyphStyle.Regular;
                        if (attr.Flags.HasFlag(CellFlags.Bold) && attr.Flags.HasFlag(CellFlags.Italic))
                            style = GlyphAtlas.GlyphStyle.BoldItalic;
                        else if (attr.Flags.HasFlag(CellFlags.Bold))
                            style = GlyphAtlas.GlyphStyle.Bold;
                        else if (attr.Flags.HasFlag(CellFlags.Italic))
                            style = GlyphAtlas.GlyphStyle.Italic;

                        glyphInfo = _atlas.GetGlyph(cell.Character, style);
                    }

                    // Build flags
                    uint flags = 0;
                    if (attr.Flags.HasFlag(CellFlags.Underline)) flags |= CellBuffer.FLAG_UNDERLINE;
                    if (attr.Flags.HasFlag(CellFlags.Strikethrough)) flags |= CellBuffer.FLAG_STRIKETHROUGH;
                    if (attr.Flags.HasFlag(CellFlags.Dim)) flags |= CellBuffer.FLAG_DIM;
                    if (cell.Width == 2) flags |= CellBuffer.FLAG_WIDE;

                    // Selection
                    if (selection.HasSelection && selection.IsSelected(visRow, col, scrollbackOffset, scrollbackCount))
                        flags |= CellBuffer.FLAG_SELECTED;

                    // Search highlights
                    if (searchMatchSet != null && searchMatchSet.Contains((visRow, col)))
                        flags |= CellBuffer.FLAG_SEARCH_MATCH;
                    if (currentMatchSet != null && currentMatchSet.Contains((visRow, col)))
                        flags |= CellBuffer.FLAG_CURRENT_MATCH;

                    // URL hover
                    if (hoveredUrl is { } url && visRow == url.row && col >= url.startCol && col <= url.endCol)
                        flags |= CellBuffer.FLAG_URL_HOVER;

                    // Cursor
                    uint cStyle = 0;
                    if (!isScrollback && bufferRow == buffer.CursorRow && col == buffer.CursorCol && cursorVisible)
                    {
                        flags |= CellBuffer.FLAG_CURSOR;
                        cStyle = (cursorStyle ?? "bar").ToLowerInvariant() switch
                        {
                            "block" => 1,
                            "bar" => 2,
                            "underline" => 3,
                            _ => 2,
                        };
                    }

                    var cellData = new CellBuffer.CellData
                    {
                        Foreground = new Color4(fg.R / 255f, fg.G / 255f, fg.B / 255f,
                            cellFg.IsDefault ? 1f : 1f),
                        Background = new Color4(bg.R / 255f, bg.G / 255f, bg.B / 255f,
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

        // --- GPU rendering (no lock held) ---
        _cellBuffer.Upload(_context);

        // Update constant buffer
        var constants = new ConstantBufferData
        {
            CellSize = new Vector2(_cellWidth, _cellHeight),
            GridSize = new Vector2(_cols, _rows),
            AtlasSize = new Vector2(_atlas.AtlasWidth, _atlas.AtlasHeight),
            ViewportSize = new Vector2(_pixelWidth, _pixelHeight),
            GridOffset = new Vector2(HorizontalPadding, 0),
            CursorAlpha = cursorAlpha,
            BellAlpha = bellAlpha,
        };

        var mapped = _context.Map(_constantBuffer, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constantBuffer, 0);

        // Set pipeline state
        _context.OMSetRenderTargets(_rtv);
        _context.RSSetViewport(0, 0, _pixelWidth, _pixelHeight);

        // Clear with background color
        var bgColor = _theme.Background;
        _context.ClearRenderTargetView(_rtv,
            new Color4(bgColor.R / 255f, bgColor.G / 255f, bgColor.B / 255f, 1f));

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null); // Vertex data generated in shader

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.VSSetShaderResource(0, _cellBuffer.SRV);

        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _cellBuffer.SRV);
        _context.PSSetShaderResource(1, _atlas.AtlasSRV);
        _context.PSSetSampler(0, _sampler);

        // Single instanced draw call for the entire grid
        _context.DrawInstanced(6, _cellBuffer.CellCount, 0, 0);

        // Present
        var hr = _swapChain.Present(1, PresentFlags.None); // VSync
        if (hr == Vortice.DXGI.ResultCode.DeviceRemoved || hr == Vortice.DXGI.ResultCode.DeviceReset)
        {
            HandleDeviceLost();
        }
    }

    public void ResizeSwapChain(int pixelWidth, int pixelHeight, int cols, int rows,
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

        _swapChain.ResizeBuffers(0, pixelWidth, pixelHeight, Format.Unknown, SwapChainFlags.None);
        CreateRenderTarget();

        _cellBuffer.Resize(cols, rows);
    }

    public void UpdateTheme(GhosttyTheme theme) => _theme = theme;

    public void UpdateFont(string fontFamily, float fontSize, float dpi,
        float cellWidth, float cellHeight)
    {
        _fontSize = fontSize;
        _dpi = dpi;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;

        _atlas?.Dispose();
        _atlas = new GlyphAtlas(_device, fontFamily, fontSize, dpi,
            (int)Math.Ceiling(cellWidth), (int)Math.Ceiling(cellHeight));
    }

    private void HandleDeviceLost()
    {
        // Full teardown and recreation would happen here.
        // For now, log and let the next frame retry.
        System.Diagnostics.Debug.WriteLine("[D3DTerminalRenderer] Device lost, needs recreation");
    }

    private static TerminalCell GetCell(int col, bool isScrollback, TerminalCell[]? scrollbackLine,
        int bufferRow, TerminalBuffer buffer)
    {
        if (isScrollback)
            return (scrollbackLine != null && col < scrollbackLine.Length) ? scrollbackLine[col] : TerminalCell.Empty;
        if (bufferRow >= 0 && bufferRow < buffer.Rows && col < buffer.Cols)
            return buffer.CellAt(bufferRow, col);
        return TerminalCell.Empty;
    }

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
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Rendering/D3DTerminalRenderer.cs
git commit -m "feat(rendering): add D3DTerminalRenderer orchestrating GPU pipeline"
```

---

## Task 8: Integrate GPU Renderer into TerminalControl

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`

This is the main migration step: replace DrawingVisual rendering with the D3D11 pipeline.

- [ ] **Step 1: Add new fields and remove old DrawingVisual fields**

In `TerminalControl.cs`, replace the rendering fields (lines 28-30, 77-93) with:

```csharp
// GPU renderer
private D3DRenderHost? _renderHost;
private Rendering.D3DTerminalRenderer? _gpuRenderer;
private bool _gpuInitialized;
```

Remove these fields:

- `_bgVisual`, `_rowVisuals`, `_overlayVisual` (lines 28-30)
- `_brushCache`, `_penCache` (lines 77-78)
- `_typefaceBold`, `_typefaceItalic`, `_typefaceBoldItalic` (lines 79-81)
- `_textRunBuffer` (line 82)
- `_glyphTypeface*`, `_glyphCache*` (lines 86-93)

Add using:

```csharp
using Cmux.Rendering;
```

- [ ] **Step 2: Replace constructor initialization**

Replace DrawingVisual creation (lines 161-167) in the constructor with:

```csharp
_renderHost = new D3DRenderHost();
AddVisualChild(_renderHost);
AddLogicalChild(_renderHost);
```

Remove `AddVisualChild` calls for `_bgVisual`, `_rowVisuals`, `_overlayVisual`.

- [ ] **Step 3: Replace VisualChildrenCount and GetVisualChild**

Replace lines 1987-1994 with:

```csharp
protected override int VisualChildrenCount => _renderHost != null ? 1 : 0;

protected override Visual GetVisualChild(int index)
{
    if (index == 0 && _renderHost != null) return _renderHost;
    throw new ArgumentOutOfRangeException(nameof(index));
}
```

- [ ] **Step 4: Replace OnCompositionTargetRendering**

Replace the `OnCompositionTargetRendering` handler (lines 336-341) and `Render()` method (lines 504-653) with:

```csharp
private void OnCompositionTargetRendering(object? sender, EventArgs e)
{
    if (!_needsRender && !_forceFullRedraw) return;
    _needsRender = false;

    if (_session == null) return;

    // Lazy GPU init (needs HWND to be ready)
    if (!_gpuInitialized && _renderHost?.Hwnd != nint.Zero)
    {
        InitializeGpuRenderer();
        _gpuInitialized = true;
    }

    if (_gpuRenderer == null || !_gpuRenderer.IsInitialized) return;

    float cursorAlpha = (_cursorVisible || !_cursorBlink) && IsPaneFocused ? 1f : 0f;
    float bellAlpha = DateTime.UtcNow < _bellFlashUntil
        ? (float)(_bellFlashUntil - DateTime.UtcNow).TotalMilliseconds / 150f
        : 0f;

    int scrollbackOffset = _session.Buffer.ScrollbackCount + _scrollOffset - 0; // visRow offset

    _gpuRenderer.Render(
        _session,
        _scrollOffset,
        _rows,
        cursorAlpha,
        bellAlpha,
        _selection,
        scrollbackOffset,
        _searchMatchSetCache,
        _currentMatchSetCache,
        _hoveredUrl,
        _cursorStyle,
        _cursorVisible);

    _forceFullRedraw = false;
}

private void InitializeGpuRenderer()
{
    var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    int pw = (int)(ActualWidth * dpi);
    int ph = (int)(ActualHeight * dpi);
    if (pw <= 0 || ph <= 0) return;

    _gpuRenderer = new Rendering.D3DTerminalRenderer();
    _gpuRenderer.Initialize(
        _renderHost!.Hwnd, pw, ph,
        _theme.FontFamily, (float)_fontSize, (float)dpi,
        _cols, _rows, (float)_cellWidth, (float)_cellHeight,
        _theme);
}
```

- [ ] **Step 5: Update OnRenderSizeChanged for GPU resize**

In `OnRenderSizeChanged` (around line 400), add after the existing grid size calculation:

```csharp
if (_gpuRenderer?.IsInitialized == true)
{
    var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    int pw = (int)(ActualWidth * dpi);
    int ph = (int)(ActualHeight * dpi);
    if (pw > 0 && ph > 0)
        _gpuRenderer.ResizeSwapChain(pw, ph, _cols, _rows, (float)_cellWidth, (float)_cellHeight);
}
```

- [ ] **Step 6: Remove old rendering methods**

Delete these methods entirely:

- `Render()` (lines 504-653)
- `RenderRow()` (lines 659-838)
- `FlushGlyphRun()` (lines 844-908)
- `FlushTextRun()` (lines 914-977)
- `GetCachedBrush()` (lines 410-419)
- `GetCachedPen()` (lines 421-428)
- `InvalidateRenderCaches()` (lines 430-439)
- `InitGlyphTypefaces()` (lines 441-452)
- `ResolveGlyphTypeface()` (lines 454-472)
- `ResolveGlyphCache()` (lines 474-481)
- `LookupGlyph()` (lines 483-492)
- `GetTypeface()` (lines 496-502)
- `ReallocateRowVisuals()` (lines 379-397)

Keep: `CalculateCellSize()`, `CalculateTerminalSize()`, `ToWpfColor()`, `WcagContrastRatio()`, `GetCell()` (static helper, also in renderer but may still be used elsewhere).

- [ ] **Step 7: Add Dispose cleanup**

In the control's cleanup/unloaded handler, add:

```csharp
_gpuRenderer?.Dispose();
_gpuRenderer = null;
```

- [ ] **Step 8: Build and verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds with no errors. There may be warnings for unused fields that were part of the old rendering pipeline — clean those up.

- [ ] **Step 9: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs
git commit -m "feat(rendering): replace WPF DrawingVisual with D3D11 GPU renderer"
```

---

## Task 9: DPI Change Handling

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`

- [ ] **Step 1: Add DPI change handler**

In the constructor or loaded handler, subscribe to DPI changes:

```csharp
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
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Cmux/Cmux.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs
git commit -m "feat(rendering): handle DPI changes with atlas invalidation"
```

---

## Task 10: Manual Testing & Validation

- [ ] **Step 1: Launch the app**

Run: `dotnet run --project src/Cmux/Cmux.csproj`
Expected: Terminal window opens with D3D11-rendered text.

- [ ] **Step 2: Test basic text output**

Type commands in the terminal (`dir`, `echo hello`, `git log`).
Verify: Text renders correctly with proper colors and alignment.

- [ ] **Step 3: Test fast output**

Run: `cat` a large file or `git log --oneline` on a large repo.
Verify: Smooth scrolling, no stutter, no missing glyphs.

- [ ] **Step 4: Test text attributes**

Run a program that outputs bold, italic, underline, colors (e.g., `ls --color`, or use ANSI escape sequences).
Verify: All attributes render correctly.

- [ ] **Step 5: Test cursor styles**

Switch between block, bar, underline cursor styles.
Verify: Cursor renders correctly and blinks.

- [ ] **Step 6: Test selection**

Select text with mouse drag.
Verify: Selection highlight appears correctly over text.

- [ ] **Step 7: Test search highlights**

Use the search feature (Ctrl+F or equivalent).
Verify: Matches highlighted in yellow, current match in orange.

- [ ] **Step 8: Test scrollback**

Scroll up through history.
Verify: Scrollback lines render correctly, scrollbar visible.

- [ ] **Step 9: Test resize**

Resize the terminal window.
Verify: Terminal re-renders correctly with new dimensions, no artifacts.

- [ ] **Step 10: Test multi-monitor DPI**

Drag window between monitors with different DPI (if available).
Verify: Text re-renders at correct DPI, no blurriness.

- [ ] **Step 11: Final commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(rendering): address issues found during GPU renderer testing"
```
