# GPU Terminal Renderer — Direct3D 11 + DirectWrite

**Date:** 2026-03-21
**Status:** Draft
**Supersedes:** 2026-03-20-terminal-rendering-perf-design.md (WPF optimizations reached ceiling)

## Problem

The WPF `DrawingVisual` + `GlyphRun` renderer in `TerminalControl.cs` has hit its performance ceiling. Despite per-row dirty tracking, ArrayPool, glyph caching, and 2-pass rendering, WPF's CPU-composited pipeline cannot match GPU-native terminals like WezTerm.

**Root causes:**

- Each `DrawGlyphRun()` goes through WPF's full layout/render/composition pipeline before reaching the GPU
- WPF `DrawingVisual` is CPU-composited (MilCore/D3D9), not GPU-native
- .NET GC pauses cause micro-stutter on frames with many allocations (Brush, Pen, Rect, GlyphRun lists)
- No texture atlas — every text run is rasterized and submitted separately per frame

**Goal:** Achieve WezTerm-level fluidity by rendering directly on the GPU via Direct3D 11, using the same architecture as Windows Terminal's AtlasEngine.

## Architecture Overview

```
┌──────────────────────────────────────────────────┐
│  WPF App (MainWindow, tabs, splits, overlays)    │
│                                                   │
│  ┌────────────────────────────────────────────┐  │
│  │  TerminalControl (FrameworkElement)         │  │
│  │  - Hosts HwndHost with D3D11 SwapChain     │  │
│  │  - Handles keyboard/mouse input (unchanged)│  │
│  │  - Delegates rendering to D3DTerminalRenderer │
│  └────────────────────────────────────────────┘  │
│                                                   │
│  ┌────────────────────────────────────────────┐  │
│  │  D3DTerminalRenderer                       │  │
│  │  - Owns ID3D11Device + IDXGISwapChain1     │  │
│  │  - Manages GlyphAtlas + CellBuffer         │  │
│  │  - Executes 1 draw call per frame          │  │
│  │  - Present1 with dirty rects               │  │
│  └────────────────────────────────────────────┘  │
│                                                   │
│  ┌────────────────────────────────────────────┐  │
│  │  GlyphAtlas                                │  │
│  │  - DirectWrite rasterization (ClearType)   │  │
│  │  - ID3D11Texture2D atlas (B8G8R8A8)        │  │
│  │  - Cache: (codepoint, style) → UV rect     │  │
│  └────────────────────────────────────────────┘  │
│                                                   │
│  TerminalSession / TerminalBuffer / VtParser     │
│  ── unchanged ──                                  │
└──────────────────────────────────────────────────┘
```

**What changes:**

- `TerminalControl`: rendering code removed, replaced by `D3DTerminalRenderer` delegation
- New: `D3DTerminalRenderer`, `GlyphAtlas`, `CellBuffer`, `TerminalShader.hlsl`

**What does NOT change:**

- `TerminalSession`, `TerminalBuffer`, `VtParser`, `TerminalProcess`, `PseudoConsole`, `OscHandler`
- All keyboard/mouse/scroll input handling in `TerminalControl`
- All WPF UI outside the terminal (MainWindow, tabs, splits, settings)

## Component Design

### 1. D3DTerminalRenderer

**Responsibility:** Owns the D3D11 device, orchestrates the render pipeline.

**Initialization:**

1. Create `ID3D11Device` + `ID3D11DeviceContext` (feature level 11_0, hardware adapter)
2. Create `IDXGISwapChain1` attached to the `HwndHost` HWND via `IDXGIFactory2.CreateSwapChainForHwnd`
3. Compile and cache vertex + pixel shaders (pre-compiled `.cso` embedded resources, runtime compilation as dev fallback)
4. Create `GlyphAtlas` and `CellBuffer`

**Per-frame render (called from `CompositionTarget.Rendering`):**

1. Check `_needsRender` flag (volatile, coalescing flag only — NOT a synchronization mechanism)
2. Acquire `RenderLock` (same lock used by VtParser thread for buffer writes)
3. Update `CellBuffer` for dirty rows only — iterate visible viewport cells (scrollback + live), resolve colors, lookup atlas UV
4. Release `RenderLock`
5. Rasterize any new glyphs into `GlyphAtlas` (on-demand, cached)
6. Bind atlas texture + cell buffer + constant buffer
7. `DrawInstanced(6, cols * rows)` — 1 draw call renders entire terminal grid
8. Second mini draw call for scrollbar overlay (12 vertices)
9. `Present1` with dirty rects

**Resize handling:**

- Recreate swap chain buffers to match new pixel dimensions (accounting for DPI)
- Recreate `CellBuffer` if grid dimensions changed
- Mark all rows dirty

**Device lost recovery:**

- After each `Present1`, check HRESULT for `DXGI_ERROR_DEVICE_REMOVED` or `DXGI_ERROR_DEVICE_RESET`
- On device lost: dispose entire pipeline (device, swap chain, atlas, buffers), recreate from scratch
- Log the removal reason via `ID3D11Device.GetDeviceRemovedReason()` for diagnostics

**Cleanup:**

- `IDisposable` — release all D3D11 COM objects in deterministic order

### 2. GlyphAtlas

**Responsibility:** Rasterize glyphs via DirectWrite, manage GPU texture atlas.

**Atlas texture:**

- `ID3D11Texture2D`, `DXGI_FORMAT_B8G8R8A8_UNORM` for ClearType text — stores 3 subpixel coverage values in R, G, B channels. This is required because ClearType produces per-subpixel alpha and cannot be stored in a single-channel format.
- Secondary atlas `DXGI_FORMAT_B8G8R8A8_UNORM` for color emoji (premultiplied alpha)
- Initial size: 2048x2048, grow to 4096x4096 if needed
- 1px padding between glyphs to prevent GPU sampling bleed

**Glyph rasterization pipeline:**

1. `IDWriteFactory.CreateTextFormat()` for each style variant (regular, bold, italic, bold+italic)
2. For each new glyph: `IDWriteFontFace.GetGlyphIndices()` → `IDWriteGlyphRunAnalysis` → `CreateAlphaTexture(DWRITE_TEXTURE_CLEARTYPE_3x1)`
3. The resulting bitmap has 3 bytes per pixel (R, G, B coverage). Upload to atlas as BGRA with A=255.
4. Store UV coordinates in cache dictionary
5. For missing codepoints in the primary font: use `IDWriteFontFallback.MapCharacters()` to resolve the correct font, rasterize from that font face

**Cache key:** `(uint codepoint, GlyphStyle style)` — font size and DPI are NOT part of the key. Instead, the entire atlas is invalidated and rebuilt when font size or DPI changes (these events are rare: settings change, window dragged to different-DPI monitor).

**Atlas full strategy:**

- When no more space: allocate a new atlas texture, re-rasterize ALL currently visible glyphs in a single frame, then swap the old atlas out
- This avoids visible artifacts (missing glyphs for 1-2 frames)
- This is rare (>10k unique styled glyphs needed to fill 4096x4096 with typical monospace cell sizes)

**Packing:** Simple row-based packing. Track current row Y and next X position. When X exceeds width, advance to next row. Glyph cells are fixed-size (cellWidth x cellHeight), so packing is trivial for a monospace terminal.

**Wide characters (CJK):** Characters with `TerminalCell.Width == 2` are rasterized into a double-width slot (2 × cellWidth). The cache key includes width implicitly via the codepoint (CJK codepoints always produce width-2 glyphs).

### 3. CellBuffer

**Responsibility:** CPU-side cell data array representing the visible viewport, uploaded to GPU as a structured buffer.

**Structure per cell (64 bytes, 16-byte aligned for GPU structured buffers):**

```hlsl
struct CellData
{
    float4 foreground;   // (R, G, B, A) normalized [0..1]         — 16 bytes
    float4 background;   // (R, G, B, A) normalized [0..1]         — 16 bytes
    float4 atlasUV;      // (u0, v0, u1, v1) in atlas texture      — 16 bytes
    uint   flags;        // bit 0: cursor, bit 1: selected,        — 4 bytes
                         // bit 2: search match, bit 3: current match,
                         // bit 4: underline, bit 5: strikethrough,
                         // bit 6: dim, bit 7: url hover,
                         // bit 8: wide char (width=2), bit 9: wide placeholder (skip)
    uint   cursorStyle;  // 0=none, 1=block, 2=bar, 3=underline    — 4 bytes
    uint   _pad0;        //                                         — 4 bytes
    uint   _pad1;        //                                         — 4 bytes
};                       // Total: 64 bytes (4 × float4, GPU-friendly)
```

**Viewport-based population:**

The CellBuffer represents the **visible viewport**, not the raw TerminalBuffer. Population iterates rows from `viewStartLine` to `viewStartLine + rows`, resolving scrollback lines via `buffer.GetScrollbackLine()` and live rows via `buffer.CellAt()` — exactly like the current `RenderRow` logic. The `_scrollOffset` state determines which lines are visible.

**Update strategy:**

- CPU-side `CellData[]` array of `cols * rows` elements (pinned, reused across frames)
- On dirty row: iterate row cells, resolve colors (inverse, WCAG contrast), lookup atlas UV, write to array
- Upload to GPU via `Map(D3D11_MAP_WRITE_DISCARD)` for full update or `Map(D3D11_MAP_WRITE_NO_OVERWRITE)` for partial row updates
- The structured buffer is bound as `StructuredBuffer<CellData>` in the shader via SRV
- Bandwidth per dirty row: `cols × 64 bytes` (e.g., 200 cols = 12.8 KB per dirty row)

### 4. TerminalShader.hlsl

**Vertex Shader:**

```
Input: SV_VertexID (0..5) + SV_InstanceID (0..cols*rows-1)
- Instance ID → (row, col) via integer division/modulo
- Vertex ID → which corner of the quad (2 triangles = 6 vertices)
- Wide chars (flag bit 8): quad width = 2 × cellSize.x
- Wide placeholders (flag bit 9): degenerate quad (zero area), skipped by GPU
- Output: screen position, atlas UV, cell colors, flags

Constant buffer:
- float2 cellSize (in pixels)
- float2 gridSize (cols, rows)
- float2 atlasSize (texture dimensions)
- float2 viewportSize (pixels)
- float2 gridOffset (horizontal/vertical padding, e.g., 20px left margin)
- float  cursorAlpha (for blink animation, 0.0..1.0)
- float  bellAlpha (for visual bell flash, 0.0..1.0, decays over time)
```

**Pixel Shader:**

```
1. Output background color (from CellData.background)
2. If atlasUV is valid (non-zero):
   a. Sample atlas texture (B8G8R8A8)
   b. ClearType blend per lhecker/dwrite-hlsl:
      - Linearize foreground and background colors (pow 2.2)
      - For each subpixel channel (R, G, B):
        output.R = lerp(bg.R, fg.R, atlas.R)  // atlas.R = red subpixel coverage
        output.G = lerp(bg.G, fg.G, atlas.G)
        output.B = lerp(bg.B, fg.B, atlas.B)
      - Re-apply gamma (pow 1/2.2)
   c. Apply dim flag (reduce alpha to 0.5)
3. If CURSOR flag set (and cursorAlpha > 0):
   a. Block (cursorStyle=1): blend cursor color over entire cell
   b. Bar (cursorStyle=2): blend 2px-wide rect at left edge
   c. Underline (cursorStyle=3): blend 2px-high rect at bottom edge
4. If SELECTED flag: blend with selection color (alpha 0.4)
5. If SEARCH_MATCH flag: blend with yellow highlight (alpha 0.4)
6. If CURRENT_MATCH flag: blend with orange highlight (alpha 0.7)
7. If UNDERLINE flag: draw 1px line at baseline
8. If STRIKETHROUGH flag: draw 1px line at mid-height
9. If URL_HOVER flag: draw 1px underline in accent color
10. If bellAlpha > 0: additive blend white (bellAlpha * 0.3) over entire output
```

### 5. WPF Integration via HwndHost

**Primary approach: HwndHost with dedicated SwapChain** (same approach as Windows Terminal)

The `TerminalControl` creates a `D3DRenderHost : HwndHost` that:

1. Creates a child HWND via `CreateWindowEx` (WS_CHILD, no border)
2. The `D3DTerminalRenderer` creates `IDXGISwapChain1` attached to this HWND
3. The HWND is sized/positioned to fill the TerminalControl bounds
4. Input events (keyboard, mouse) are forwarded from the HwndHost to the TerminalControl

**Airspace mitigation:**

WPF HwndHost creates an "airspace" issue where WPF elements cannot render on top of the hosted HWND. For cmux this is acceptable because:

- The terminal grid fills the entire control area — no WPF overlays need to render on top
- Notification ring and focus indicator borders are rendered **inside** the D3D11 pipeline (via the shader or additional draw calls), not as WPF elements
- Popups/tooltips from other parts of the app (tabs, menus) render in their own top-level HWND, unaffected

**Alternative (D3DImage):** If HwndHost proves problematic (e.g., focus issues with splits), `D3DImage` with D3D9Ex shared surface is the fallback. Requires `IDirect3DDevice9Ex` + `D3D11_RESOURCE_MISC_SHARED` + `IDXGIResource.SharedHandle`. Known issues: fragile on multi-GPU laptops (Intel+NVIDIA), `Lock/Unlock` adds CPU-GPU sync per frame.

### 6. Scrollbar & Visual Effects

**Scrollbar** — rendered as a second mini draw call after the main terminal grid:

- **Track:** 1 semi-transparent quad (full height, 6px wide, right edge)
- **Thumb:** 1 opaque quad (position/height from scroll state)
- **Scrollback indicator:** small text badge rendered as pre-rasterized glyphs from the atlas

Total: 12-18 additional vertices. Negligible cost.

**Visual bell:** Controlled by `bellAlpha` in the constant buffer. When BEL is received, set `bellAlpha = 1.0` and decay to 0 over ~150ms via the cursor blink timer. The pixel shader applies an additive white flash.

**Notification ring + focus indicator:** Rendered as colored border quads inside the D3D11 pipeline (4 thin rectangles along the edges). No WPF `Border` element needed.

## Threading Model

```
┌─────────────────────┐     ┌──────────────────────┐
│  Terminal-Read Thread │     │  WPF UI Thread        │
│  (VtParser)           │     │  (Dispatcher)          │
│                       │     │                        │
│  ConPTY output →      │     │  CompositionTarget     │
│  VtParser.Feed() →    │     │  .Rendering event →    │
│  TerminalBuffer write  │     │  D3DTerminalRenderer   │
│  (under RenderLock)   │     │  .Render()             │
│  _needsRender = true  │     │  (acquires RenderLock  │
│                       │     │   to read buffer,      │
│                       │     │   updates CellBuffer,  │
│                       │     │   releases lock,       │
│                       │     │   then GPU draw call)  │
└─────────────────────┘     └──────────────────────┘
```

- `RenderLock` (existing `object` lock from `TerminalSession`) is **preserved** and protects all `TerminalBuffer` reads during `CellBuffer` population
- `_needsRender` volatile flag serves as a coalescing mechanism only — it does NOT provide thread safety
- GPU draw calls happen AFTER `RenderLock` is released (no lock held during GPU work)
- `GlyphAtlas` rasterization happens on the UI thread (DirectWrite is not thread-safe by default)

## DPI and Multi-Monitor Handling

- Listen to `DpiChanged` event on the `FrameworkElement` (or `HwndHost`)
- On DPI change:
  1. Recalculate `_cellWidth`, `_cellHeight` for the new DPI
  2. Resize the swap chain buffers to the new pixel dimensions
  3. **Invalidate the entire GlyphAtlas** (glyphs rasterized at old DPI are wrong at new DPI)
  4. Recreate `CellBuffer` if grid dimensions changed
  5. Mark all rows dirty for full redraw
- The atlas rebuilds lazily: only visible glyphs are re-rasterized on the next frame

## Migration Strategy

### What to keep from current TerminalControl

1. All input handling: `OnKeyDown`, `OnTextInput`, `OnMouseDown/Move/Up`, `OnMouseWheel`
2. URL detection logic (`_hoveredUrl`, `_cachedRowUrls`)
3. Search highlight management (`_searchMatches`, `_currentSearchMatch`)
4. Selection model (`TerminalSelection`)
5. Cursor blink timer (`_cursorTimer`)
6. Scroll state (`_scrollOffset`, `_followOutput`)
7. All public API / events (`FocusRequested`, `HasNotification`, etc.)
8. Visual bell timer (`_bellTimer`, `_bellFlashUntil`)

### What to remove

1. `DrawingVisual _bgVisual, _rowVisuals[], _overlayVisual` — replaced by D3D11 pipeline
2. `Render()` method — replaced by `D3DTerminalRenderer.Render()`
3. `RenderRow()` method — replaced by `CellBuffer` update
4. `FlushGlyphRun()`, `FlushTextRun()` — replaced by atlas-based rendering
5. `GetTypeface()`, `ResolveGlyphTypeface()` — replaced by DirectWrite in `GlyphAtlas`
6. All `_brushCache`, `_penCache` — no longer needed (GPU handles colors)
7. `_typefaceBold/Italic/BoldItalic`, `_glyphCache*` dictionaries — replaced by `GlyphAtlas`

### File structure

```
src/Cmux/
  Controls/
    TerminalControl.cs          # Modified: input only, delegates render
    D3DRenderHost.cs            # New: HwndHost subclass for D3D11 HWND
  Rendering/
    D3DTerminalRenderer.cs      # New: D3D11 pipeline orchestration
    GlyphAtlas.cs               # New: DirectWrite rasterization + atlas
    CellBuffer.cs               # New: CPU cell array + GPU upload
    ShaderLoader.cs             # New: loads pre-compiled .cso shaders
  Shaders/
    TerminalShader.hlsl         # New: vertex + pixel shader (source)
    TerminalVS.cso              # New: pre-compiled vertex shader (embedded resource)
    TerminalPS.cso              # New: pre-compiled pixel shader (embedded resource)
```

## Shader Compilation Strategy

- **Production:** Shaders are pre-compiled to `.cso` bytecode at build time via an MSBuild target (`fxc.exe` from Windows SDK). The `.cso` files are embedded as resources in the assembly.
- **Development:** `ShaderLoader` falls back to runtime compilation via `Vortice.D3DCompiler` (`D3DCompile`) if `.cso` resources are missing. This allows iterating on shaders without rebuilding.
- Shader compilation errors at runtime are logged with full HLSL error messages for diagnostics.

## Dependencies

**New NuGet packages:**

- `Vortice.Direct3D11` — D3D11 device, textures, buffers, shaders
- `Vortice.DirectWrite` — glyph rasterization (ClearType)
- `Vortice.DXGI` — swap chain, surface sharing
- `Vortice.D3DCompiler` — HLSL runtime compilation (dev fallback)

All Vortice packages are MIT-licensed, actively maintained, target .NET 9+.

Note: `Vortice.Direct3D9` is NOT needed if using HwndHost approach (no D3D9Ex interop required).

## Performance Expectations

| Metric                 | Current (WPF)                            | Target (D3D11)                                                 |
| ---------------------- | ---------------------------------------- | -------------------------------------------------------------- |
| Draw calls per frame   | ~rows × 2 (bg + text per row)            | 1-2 (entire grid + scrollbar)                                  |
| Glyph rasterization    | Every `FlushGlyphRun` call               | Once per unique glyph (cached in atlas)                        |
| CPU→GPU data per frame | Entire DrawingVisual tree                | Only dirty row CellData (~cols × 64 bytes per dirty row)       |
| GC pressure            | Moderate (GlyphRun lists, Span.ToArray)  | Minimal (fixed buffers, no managed allocations in render loop) |
| Frame budget (60fps)   | ~16ms, often exceeded during fast output | <2ms expected (GPU-bound, 1 draw call)                         |
| Text quality           | WPF GlyphRun (grayscale AA)              | DirectWrite ClearType (subpixel AA)                            |

## Non-Goals

- Cross-platform support (this is Windows-only, matching cmux's WPF nature)
- Ligature support (monospace terminal, not needed for v1)
- HDR rendering
- Variable-width fonts
