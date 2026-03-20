# Terminal Rendering Performance Overhaul

**Date:** 2026-03-20
**Status:** Draft

## Problem

The terminal rendering has three critical issues:

1. **3 FPS with heavy output** — `FormattedText` allocation per text run per frame + full O(rows\*cols) redraw on every output chunk
2. **Character misalignment** — `FormattedText` uses proportional spacing, causing characters like `≈`, box-drawing, and Unicode symbols to shift subsequent text off the grid
3. **GC pressure** — hundreds of `FormattedText` objects + `GetLine()` array allocations per second

WezTerm/Alacritty avoid all this via GPU glyph atlas + dirty-cell tracking. Our approach: stay in WPF but replace `FormattedText` with `GlyphRun` + add frame limiting + dirty-row tracking.

## Design

### 1. Frame Limiter — CompositionTarget.Rendering

**Current:** `output chunk → Redraw?.Invoke() → RequestRender() → Dispatcher.BeginInvoke(Render)`
Each 4KB chunk triggers a render attempt. With fast output, hundreds of renders/sec.

**New:** Decouple output from rendering.

```
output chunk → _needsRender = true  (atomic flag, no dispatch)
CompositionTarget.Rendering → if (_needsRender) { _needsRender = false; Render(); }
```

- `CompositionTarget.Rendering` fires once per WPF composition frame (~60fps, vsync-synced)
- All output between frames is coalesced into one render
- `RequestRender()` still exists for explicit redraws (selection, search, resize) — it just sets the flag
- The cursor blink timer also just sets the flag instead of dispatching

**Files:**

- `TerminalControl.cs`: Replace `_renderQueued` + `Dispatcher.BeginInvoke` with `_needsRender` flag + `CompositionTarget.Rendering` handler
- `TerminalSession.cs`: `Redraw?.Invoke()` stays — the handler in TerminalControl just sets a flag

### 2. Row-Level Dirty Tracking with Cached Drawing

**Current:** `TerminalCell.IsDirty` exists per cell but is never read by the renderer. Every frame iterates all rows×cols.

**Constraint:** `DrawingVisual.RenderOpen()` clears all previous drawing content. We cannot "skip" a row and expect the previous frame's content to remain. Every row must be drawn every frame.

**New:** Track dirty rows at the buffer level. Cache per-row render data (GlyphRuns + background rects). Dirty rows rebuild their cache; clean rows replay from cache.

Add to `TerminalBuffer`:

```csharp
private bool[] _dirtyRows;       // per-row dirty flag
private bool _allDirty = true;   // force full repaint (resize, restore, scroll)

public bool IsRowDirty(int row) => _allDirty || _dirtyRows[row];
public void MarkRowDirty(int row) { if (row >= 0 && row < Rows) _dirtyRows[row] = true; }
public void MarkAllDirty() { _allDirty = true; }
public void ClearDirtyFlags() { _allDirty = false; Array.Clear(_dirtyRows); }
```

`_dirtyRows` must be reallocated in `Resize()` with `_allDirty = true`.

Mark rows dirty in: `SetChar()`, `WriteChar()`, `WriteString()`, `InsertLine()`, `DeleteLine()`, `ScrollUp()`, `ScrollDown()`, `Clear()`, `EraseLine()`, `EraseDisplay()`, `EraseChars()`, `InsertChars()`, `DeleteChars()`, `RestoreSnapshot()`, `SwitchToAlternateScreen()`, `SwitchToMainScreen()`, `Resize()`.

Add per-row cache in `TerminalControl`:

```csharp
private struct CachedRow
{
    public List<(GlyphRun Run, Brush Brush)> GlyphRuns;
    public List<(Rect Rect, Brush Brush)> Backgrounds;
    public List<(Point From, Point To, Pen Pen)> Decorations; // underline, strikethrough
}
private CachedRow[] _rowCache;
```

In `Render()`:

- For each visible row: if dirty (or no cache), rebuild GlyphRuns/backgrounds into `_rowCache[visRow]`
- For clean rows: replay `_rowCache[visRow]` directly via `dc.DrawGlyphRun()` / `dc.DrawRectangle()`
- After render: `buffer.ClearDirtyFlags()`
- Viewport movement (scroll): invalidate all rows (`_allDirty`)
- Selection/search/cursor changes: invalidate affected visible rows in the cache

**Thread safety:** `Render()` reads the buffer on the UI thread while the read thread writes under `_lock`. The render must acquire the same lock for the duration of dirty-check + cell reads. The lock is held for <1ms at 60fps with dirty-row skipping, so contention is negligible.

Add `_lastViewStartLine` to detect viewport movement:

```csharp
if (viewStartLine != _lastViewStartLine) { _allDirty = true; }
_lastViewStartLine = viewStartLine;
```

### 3. GlyphRun Rendering (replaces FormattedText)

**Current:** `FlushTextRun()` creates `new FormattedText(string, ...)` per run per frame. FormattedText does full text shaping (expensive) and uses proportional advance widths (causes misalignment).

**New:** Use `GlyphRun` with fixed advance widths.

#### Initialization (once, in constructor or on theme change):

```csharp
private GlyphTypeface _glyphTypeface;        // normal
private GlyphTypeface _glyphTypefaceBold;    // bold
private GlyphTypeface _glyphTypefaceItalic;  // italic
private GlyphTypeface _glyphTypefaceBoldItalic;

// Lazy glyph index cache: char → glyph index (populated on first use)
private readonly Dictionary<int, ushort> _glyphCache = new();
private readonly Dictionary<int, ushort> _glyphCacheBold = new();
// ... per typeface variant

private void InitGlyphTypefaces()
{
    _typeface.TryGetGlyphTypeface(out _glyphTypeface);
    // Note: GlyphTypeface.CharacterToGlyphMap is IDictionary<int, ushort>
    // NOT Dictionary<char, ushort>. Use int keys (codepoints).
    // Lazy cache — don't copy the full 30k+ map, populate on miss.
    _glyphCache.Clear();
}

private ushort GetGlyphIndex(GlyphTypeface gt, Dictionary<int, ushort> cache, int codepoint)
{
    if (cache.TryGetValue(codepoint, out var idx)) return idx;
    if (gt.CharacterToGlyphMap.TryGetValue(codepoint, out idx))
    {
        cache[codepoint] = idx;
        return idx;
    }
    return 0; // .notdef — signals missing glyph
}
```

#### FlushTextRun replacement:

```csharp
private void FlushGlyphRun(DrawingContext dc, double dpi, double y, int startCol,
    Color fgColor, bool bold, bool italic, bool dim, bool underline, bool strikethrough,
    ReadOnlySpan<(char Char, int Width)> cells)
{
    if (cells.Length == 0) return;

    var gt = GetGlyphTypeface(bold, italic);
    var cache = GetGlyphCache(bold, italic);

    // Split runs at missing-glyph boundaries to avoid full-run FormattedText fallback
    int runStart = 0;
    while (runStart < cells.Length)
    {
        // Find contiguous segment of known glyphs
        int runEnd = runStart;
        bool hasMissing = false;
        while (runEnd < cells.Length)
        {
            var idx = GetGlyphIndex(gt, cache, cells[runEnd].Char);
            if (idx == 0 && cells[runEnd].Char != ' ') { hasMissing = true; break; }
            runEnd++;
        }

        // Render known-glyph segment with GlyphRun
        if (runEnd > runStart)
            EmitGlyphRun(dc, dpi, y, startCol, gt, cache, cells[runStart..runEnd], fgColor, dim);

        startCol += CountColumns(cells[runStart..runEnd]);

        // Render missing-glyph character(s) with FormattedText fallback (constrained to cellWidth)
        if (hasMissing && runEnd < cells.Length)
        {
            EmitFallbackChar(dc, dpi, y, startCol, cells[runEnd], fgColor, bold, italic, dim);
            startCol += cells[runEnd].Width;
            runEnd++;
        }
        runStart = runEnd;
    }

    // Decorations
    double totalWidth = CountColumns(cells) * _cellWidth;
    double x = HorizontalPadding + startCol * _cellWidth;
    if (underline) { /* cached pen */ }
    if (strikethrough) { /* cached pen */ }
}

private void EmitGlyphRun(DrawingContext dc, double dpi, double y, int startCol,
    GlyphTypeface gt, Dictionary<int, ushort> cache,
    ReadOnlySpan<(char Char, int Width)> cells, Color fgColor, bool dim)
{
    var glyphIndices = new ushort[cells.Length];
    var advanceWidths = new double[cells.Length];

    for (int i = 0; i < cells.Length; i++)
    {
        glyphIndices[i] = GetGlyphIndex(gt, cache, cells[i].Char);
        advanceWidths[i] = cells[i].Width * _cellWidth; // Width=2 for CJK wide chars
    }

    double x = HorizontalPadding + startCol * _cellWidth;
    // Baseline: use Baseline ratio applied to fontSize, offset within cell
    double baseline = y + gt.Baseline * _fontSize;

    var glyphRun = new GlyphRun(
        glyphTypeface: gt, bidiLevel: 0, isSideways: false,
        renderingEmSize: _fontSize, pixelsPerDip: (float)dpi,
        glyphIndices: glyphIndices, baselineOrigin: new Point(x, baseline),
        advanceWidths: advanceWidths, glyphOffsets: null, characters: null,
        deviceFontName: null, clusterMap: null, caretStops: null, language: null);

    var brush = dim
        ? GetCachedBrush(Color.FromArgb(128, fgColor.R, fgColor.G, fgColor.B))
        : GetCachedBrush(fgColor);
    dc.DrawGlyphRun(brush, glyphRun);
}
```

**Key advantages:**

- `advanceWidths[i] = cells[i].Width * _cellWidth` — fixes misalignment and supports wide CJK chars
- Missing glyphs are isolated to single-char FormattedText fallbacks, not entire runs
- Lazy glyph cache: only caches the ~200 chars actually used, not the full 30k+ font map
- `GlyphTypeface.CharacterToGlyphMap` is `IDictionary<int, ushort>` — correctly typed

#### Baseline verification:

`GlyphTypeface.Baseline` is the distance from top of em-square to baseline, as a fraction of em-size. So `baseline * fontSize` gives the pixel offset from cell top to text baseline. This is correct when `_cellHeight >= _fontSize`. If `_cellHeight > _fontSize` (line spacing), text will be top-aligned within the cell, which matches the current FormattedText behavior. No adjustment needed.

### 4. Background Rectangle Batching

**Current:** One `dc.DrawRectangle()` per cell with non-default background.

**New:** Merge consecutive cells on the same row with the same background color into one wider rectangle.

```csharp
// In the column loop, instead of drawing immediately:
if (!cellBg.IsDefault)
{
    if (cellBg == currentBgRun)
    {
        bgRunWidth += ceilW;  // extend current run
    }
    else
    {
        FlushBgRun(dc, ...);  // draw previous run
        // start new run
    }
}
```

This reduces draw calls from potentially 120 per row to ~3-5.

### 5. CellAt Direct Access (eliminate GetLine allocation)

**Current:** `GetScrollbackLine()` returns `TerminalCell[]` (reference to stored array — OK).
`GetLine()` allocates a new `TerminalCell[]` each call (wasteful but only used in snapshots, not rendering).

The renderer already uses `buffer.CellAt(row, col)` which returns `ref TerminalCell` — no allocation. This is fine. No change needed for live buffer access.

### 6. Lifecycle Management

- Subscribe to `CompositionTarget.Rendering` in the constructor or `OnLoaded`
- Unsubscribe in `Dispose()` / when detached from visual tree — `CompositionTarget.Rendering` is a static event, failing to unsubscribe leaks the control
- Cache Pen objects for URL hover, notification ring, focus indicator alongside the brush cache (avoid per-frame `new Pen()`)

## Architecture Summary

```
Output arrives (4KB chunks)
  │
  ├─ TerminalSession.ReadLoop/FeedOutput
  │   ├─ Parser processes VT sequences → TerminalBuffer
  │   │   └─ SetChar/Scroll/etc mark affected rows dirty
  │   └─ Redraw?.Invoke() → TerminalControl.OnRedraw()
  │       └─ _needsRender = true  (volatile flag, no dispatch)
  │
  └─ CompositionTarget.Rendering (vsync, ~60fps)
      └─ if (_needsRender) Render()
          ├─ Acquire buffer lock
          ├─ Draw background (full rect)
          ├─ For each visible row:
          │   ├─ If dirty: rebuild row cache (GlyphRuns + bg rects)
          │   ├─ If clean: replay row cache
          │   ├─ Batch background rects (merge same-color)
          │   └─ Batch text into GlyphRun (same style)
          │       └─ advanceWidths = cell.Width * _cellWidth
          ├─ Draw cursor, scrollbar
          ├─ buffer.ClearDirtyFlags()
          └─ Release buffer lock
```

## Files to Modify

| File                                          | Changes                                                                                                                               |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cmux/Controls/TerminalControl.cs`        | Replace FormattedText with GlyphRun, add CompositionTarget.Rendering frame limiter, add dirty-row skip logic, add background batching |
| `src/Cmux.Core/Terminal/TerminalBuffer.cs`    | Add `_dirtyRows[]` + `_allDirty`, mark dirty in mutation methods, expose `IsRowDirty`/`ClearDirtyFlags`                               |
| `src/Cmux.Core/Terminal/TerminalAttribute.cs` | Remove `IsDirty` from `TerminalCell` (replaced by row-level tracking)                                                                 |

## Expected Results

| Metric                     | Before                | After                         |
| -------------------------- | --------------------- | ----------------------------- |
| FPS during heavy output    | ~3                    | 60 (vsync)                    |
| FormattedText allocs/frame | 50-100+               | 0 (rare fallback only)        |
| Rows processed/frame       | ALL (30)              | Only dirty (1-5 typical)      |
| Character alignment        | Broken (proportional) | Perfect (fixed advanceWidths) |
| Draw calls for backgrounds | ~120/frame            | ~10-15/frame                  |

## Verification

1. Run `seq 1 100000` — should stay 60fps, no stuttering
2. Run `cat` on a large file — smooth scrolling
3. Check characters `≈ │ ─ ┌ ┐ └ ┘ █ ▓` — all grid-aligned, no offset
4. Verify cursor blink still works
5. Verify search highlights still render correctly
6. Verify selection rendering still works
7. Verify scrollback navigation (Page Up/Down) still works
8. Test with split panes — multiple terminals rendering simultaneously
