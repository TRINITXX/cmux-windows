// TerminalShader.hlsl
// GPU terminal renderer: renders the entire terminal grid in a single instanced draw call.
// ClearType blending follows the approach from lhecker/dwrite-hlsl (Windows Terminal).

// ---------------------------------------------------------------------------
// Constant buffer (b0)
// ---------------------------------------------------------------------------
cbuffer TerminalConstants : register(b0)
{
    float2 cellSize;      // cell dimensions in pixels
    float2 gridSize;      // (cols, rows)
    float2 atlasSize;     // atlas texture dimensions in pixels
    float2 viewportSize;  // render target dimensions in pixels
    float2 gridOffset;    // left/top padding in pixels (e.g. 20px left margin)
    float  cursorAlpha;   // 0..1 for cursor blink
    float  bellAlpha;     // 0..1 for visual bell flash
    float  scrollbarThumbTop;    // normalized 0..1 thumb position
    float  scrollbarThumbHeight; // normalized 0..1 thumb size
    float  scrollbarAlpha;       // 0 = hidden, >0 = thumb opacity
    float  _scrollPad;
};

// ---------------------------------------------------------------------------
// Cell data (t0)
// ---------------------------------------------------------------------------
struct CellData
{
    float4 foreground;   // RGBA normalized
    float4 background;   // RGBA normalized
    float4 atlasUV;      // (u0, v0, u1, v1) in atlas
    uint   flags;        // see FLAG_* constants below
    uint   cursorStyle;  // 0=none, 1=block, 2=bar, 3=underline
    uint   _pad0;
    uint   _pad1;
};

StructuredBuffer<CellData> cells : register(t0);

// ---------------------------------------------------------------------------
// Textures & samplers
// ---------------------------------------------------------------------------
// B8G8R8A8 format; R/G/B = ClearType subpixel coverage per channel
Texture2D    glyphAtlas   : register(t1);
SamplerState glyphSampler : register(s0); // point sampling

// ---------------------------------------------------------------------------
// Flag bit constants
// ---------------------------------------------------------------------------
static const uint FLAG_CURSOR           = 0x001;
static const uint FLAG_SELECTED         = 0x002;
static const uint FLAG_SEARCH_MATCH     = 0x004;
static const uint FLAG_CURRENT_MATCH    = 0x008;
static const uint FLAG_UNDERLINE        = 0x010;
static const uint FLAG_STRIKETHROUGH    = 0x020;
static const uint FLAG_DIM              = 0x040;
static const uint FLAG_URL_HOVER        = 0x080;
static const uint FLAG_WIDE             = 0x100;
static const uint FLAG_WIDE_PLACEHOLDER = 0x200;

// ---------------------------------------------------------------------------
// sRGB <-> linear helpers
// ---------------------------------------------------------------------------
float3 SRGBToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float3 LinearToSRGB(float3 c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

// ---------------------------------------------------------------------------
// VS -> PS structure
// ---------------------------------------------------------------------------
struct VSOutput
{
    float4 pos       : SV_Position;
    float2 atlasUV   : TEXCOORD0;   // interpolated UV into glyph atlas
    float2 cellUV    : TEXCOORD1;   // 0..1 within the cell (for decorations)
    float4 fg        : COLOR0;
    float4 bg        : COLOR1;
    uint   flags     : BLENDINDICES0;
    uint   cursorStyle : BLENDINDICES1;
};

// ---------------------------------------------------------------------------
// Vertex shader
// ---------------------------------------------------------------------------
// 6 vertices per instance (2 triangles, no index buffer).
// Quad corner layout (CCW winding, clip-space y-up):
//   v0=(0,0) v1=(1,0) v2=(0,1)   v3=(1,0) v4=(1,1) v5=(0,1)
static const float2 quadCorners[6] =
{
    float2(0.0, 0.0), // top-left
    float2(1.0, 0.0), // top-right
    float2(0.0, 1.0), // bottom-left
    float2(1.0, 0.0), // top-right
    float2(1.0, 1.0), // bottom-right
    float2(0.0, 1.0), // bottom-left
};

VSOutput VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    VSOutput o;

    // Decode (col, row) from instanceID
    uint col = instanceID % (uint)gridSize.x;
    uint row = instanceID / (uint)gridSize.x;

    CellData cell = cells[instanceID];

    // Wide placeholder: emit a degenerate (zero-size) quad so it is culled.
    float cellW = cellSize.x;
    float cellH = cellSize.y;
    if (cell.flags & FLAG_WIDE_PLACEHOLDER)
    {
        o.pos        = float4(0.0, 0.0, 0.0, 0.0);
        o.atlasUV    = float2(0.0, 0.0);
        o.cellUV     = float2(0.0, 0.0);
        o.fg         = cell.foreground;
        o.bg         = cell.background;
        o.flags      = cell.flags;
        o.cursorStyle = cell.cursorStyle;
        return o;
    }

    // Wide chars span 2 columns.
    if (cell.flags & FLAG_WIDE)
        cellW *= 2.0;

    // Corner in pixel space (top-left origin, y-down)
    float2 corner   = quadCorners[vertexID];
    float2 pixelPos = gridOffset
                    + float2((float)col * cellSize.x, (float)row * cellSize.y)
                    + corner * float2(cellW, cellH);

    // Extend last column's right edge to viewport edge (scrollbar gutter).
    // Only extends the position, not the UV — pixel shader skips glyph in gutter.
    if (col == (uint)gridSize.x - 1 && corner.x > 0.5)
    {
        float gridRight = gridOffset.x + gridSize.x * cellSize.x;
        if (viewportSize.x > gridRight)
            pixelPos.x = viewportSize.x;
    }

    // Snap to integer pixel boundaries to prevent sub-pixel gaps between cells
    pixelPos = floor(pixelPos + 0.5);

    // Convert to NDC (clip space): x in [-1,1], y in [1,-1]
    float2 ndc;
    ndc.x =  (pixelPos.x / viewportSize.x) * 2.0 - 1.0;
    ndc.y = -(pixelPos.y / viewportSize.y) * 2.0 + 1.0;

    o.pos = float4(ndc, 0.0, 1.0);

    // Atlas UV: interpolate between (u0,v0) and (u1,v1) using the corner
    o.atlasUV = lerp(cell.atlasUV.xy, cell.atlasUV.zw, corner);

    // Cell-local UV (0..1) for decoration placement
    o.cellUV = corner;

    o.fg         = cell.foreground;
    o.bg         = cell.background;
    o.flags      = cell.flags;
    o.cursorStyle = cell.cursorStyle;

    return o;
}

// ---------------------------------------------------------------------------
// Pixel shader
// ---------------------------------------------------------------------------
float4 PSMain(VSOutput i) : SV_Target
{
    float4 color = i.bg;

    // Detect gutter zone (pixels beyond the cell grid, used for scrollbar)
    float gridRight = gridOffset.x + gridSize.x * cellSize.x;
    bool inGutter = (i.pos.x >= gridRight);

    // ------------------------------------------------------------------
    // 1. Glyph rendering with gamma-correct ClearType blending
    //    Skip in gutter zone to prevent glyph stretching
    // ------------------------------------------------------------------
    bool hasGlyph = any(i.atlasUV > 0.001);
    if (hasGlyph && !inGutter)
    {
        // R,G,B = per-channel subpixel coverage from atlas
        float3 coverage = glyphAtlas.Sample(glyphSampler, i.atlasUV).rgb;

        // DIM flag: reduce coverage
        if (i.flags & FLAG_DIM)
            coverage *= 0.5;

        // Gamma-correct ClearType blend
        float3 fgLinear = SRGBToLinear(i.fg.rgb);
        float3 bgLinear = SRGBToLinear(i.bg.rgb);
        float3 blended  = lerp(bgLinear, fgLinear, coverage);
        color = float4(LinearToSRGB(blended), i.bg.a);
    }

    // ------------------------------------------------------------------
    // 2. Cursor overlay
    // ------------------------------------------------------------------
    if ((i.flags & FLAG_CURSOR) && cursorAlpha > 0.0)
    {
        bool onCursor = false;
        if (i.cursorStyle == 1) // block: full cell
        {
            onCursor = true;
        }
        else if (i.cursorStyle == 2) // bar: 2px at left edge
        {
            onCursor = (i.cellUV.x < 2.0 / cellSize.x);
        }
        else if (i.cursorStyle == 3) // underline: 2px at bottom edge
        {
            onCursor = (i.cellUV.y > 1.0 - 2.0 / cellSize.y);
        }

        if (onCursor)
        {
            // Cursor color is inverse of background, blended by cursorAlpha
            float3 cursorColor = 1.0 - color.rgb;
            color.rgb = lerp(color.rgb, cursorColor, cursorAlpha);
        }
    }

    // ------------------------------------------------------------------
    // 3. Selection highlight: indigo tint
    // ------------------------------------------------------------------
    if (i.flags & FLAG_SELECTED)
    {
        float3 indigo = float3(0.506, 0.549, 0.973);
        color.rgb = lerp(color.rgb, indigo, 0.4);
    }

    // ------------------------------------------------------------------
    // 4. Current search match: orange tint (applied before generic match)
    // ------------------------------------------------------------------
    if (i.flags & FLAG_CURRENT_MATCH)
    {
        float3 orange = float3(0.984, 0.573, 0.235);
        color.rgb = lerp(color.rgb, orange, 0.7);
    }

    // ------------------------------------------------------------------
    // 5. Generic search match: yellow tint
    // ------------------------------------------------------------------
    if (i.flags & FLAG_SEARCH_MATCH)
    {
        float3 yellow = float3(0.984, 0.749, 0.141);
        color.rgb = lerp(color.rgb, yellow, 0.4);
    }

    // ------------------------------------------------------------------
    // 6. Underline decoration: 1px line at bottom
    // ------------------------------------------------------------------
    if (i.flags & FLAG_UNDERLINE)
    {
        if (i.cellUV.y > 1.0 - 1.5 / cellSize.y)
            color.rgb = i.fg.rgb;
    }

    // ------------------------------------------------------------------
    // 7. Strikethrough: 1px horizontal line at mid-height
    // ------------------------------------------------------------------
    if (i.flags & FLAG_STRIKETHROUGH)
    {
        if (abs(i.cellUV.y - 0.5) < 0.75 / cellSize.y)
            color.rgb = i.fg.rgb;
    }

    // ------------------------------------------------------------------
    // 8. URL hover underline: accent color (steel blue)
    // ------------------------------------------------------------------
    if (i.flags & FLAG_URL_HOVER)
    {
        if (i.cellUV.y > 1.0 - 1.5 / cellSize.y)
        {
            float3 accent = float3(0.275, 0.545, 0.792); // steel blue
            color.rgb = accent;
        }
    }

    // ------------------------------------------------------------------
    // 9. Scrollbar overlay (rendered on top of cells at right edge)
    // ------------------------------------------------------------------
    if (scrollbarAlpha > 0.0)
    {
        float sbWidth  = 30.0;
        float sbLeft   = viewportSize.x - sbWidth - 1.0;
        float px = i.pos.x;
        float py = i.pos.y;

        if (px >= sbLeft && px < sbLeft + sbWidth)
        {
            float thumbTop = scrollbarThumbTop * viewportSize.y;
            float thumbBot = thumbTop + scrollbarThumbHeight * viewportSize.y;

            if (py >= thumbTop && py < thumbBot)
            {
                // Rounded ends: fade out top/bottom 3px
                float radius = min(3.0, scrollbarThumbHeight * viewportSize.y * 0.5);
                float distTop = py - thumbTop;
                float distBot = thumbBot - py;
                float edgeFade = saturate(min(distTop, distBot) / radius);

                float3 thumbColor = float3(0.7, 0.7, 0.75); // light gray
                color.rgb = lerp(color.rgb, thumbColor, scrollbarAlpha * 0.85 * edgeFade);
            }
        }
    }

    // ------------------------------------------------------------------
    // 10. Visual bell flash: lerp toward white
    // ------------------------------------------------------------------
    color.rgb = lerp(color.rgb, float3(1.0, 1.0, 1.0), bellAlpha * 0.3);

    return color;
}
