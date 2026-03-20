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

### 2. Row-Level Dirty Tracking

**Current:** `TerminalCell.IsDirty` exists per cell but is never read by the renderer. Every frame iterates all rows×cols.

**New:** Track dirty rows at the buffer level. The renderer skips clean rows.

Add to `TerminalBuffer`:

```csharp
private bool[] _dirtyRows;       // per-row dirty flag
private bool _allDirty = true;   // force full repaint (resize, restore, scroll)

public bool IsRowDirty(int row) => _allDirty || _dirtyRows[row];
public void MarkRowDirty(int row) { if (row >= 0 && row < Rows) _dirtyRows[row] = true; }
public void MarkAllDirty() { _allDirty = true; }
public void ClearDirtyFlags() { _allDirty = false; Array.Clear(_dirtyRows); }
```

Mark rows dirty in: `SetChar()`, `InsertLine()`, `DeleteLine()`, `ScrollUp()`, `ScrollDown()`, `Clear()`, `EraseLine()`, `EraseDisplay()`, `RestoreSnapshot()`.

In `Render()`:

- When rendering live buffer rows (not scrollback): skip rows where `!buffer.IsRowDirty(bufferRow)`
- After render: `buffer.ClearDirtyFlags()`
- Scrollback rows: always render when viewport moves (scroll offset changed), skip otherwise
- Selection, search highlights, cursor: force dirty on affected rows

**Scrollback consideration:** Scrollback lines are immutable once pushed. When the user scrolls through history, we need a full repaint (viewport changed), but when output is streaming and the user is at the bottom, only the active buffer rows change.

Add `_lastViewStartLine` to detect viewport movement:

```csharp
if (viewStartLine != _lastViewStartLine) buffer.MarkAllDirty();
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

// Glyph index cache: char → glyph index (per typeface)
private Dictionary<char, ushort> _glyphMap;
private Dictionary<char, ushort> _glyphMapBold;
// ... etc

private void InitGlyphTypefaces()
{
    _typeface.TryGetGlyphTypeface(out _glyphTypeface);
    // Build glyph map from CharacterToGlyphMap
    _glyphMap = new Dictionary<char, ushort>(_glyphTypeface.CharacterToGlyphMap);
    // Repeat for bold, italic, bold+italic
}
```

#### FlushTextRun replacement:

```csharp
private void FlushGlyphRun(DrawingContext dc, double dpi, double y, int startCol,
    Color fgColor, bool bold, bool italic, bool dim, bool underline, bool strikethrough)
{
    var str = _textRunBuffer.ToString();
    if (str.Length == 0) return;

    var gt = GetGlyphTypeface(bold, italic);
    var map = GetGlyphMap(bold, italic);
    var fallbackIndex = gt.CharacterToGlyphMap.GetValueOrDefault('?', (ushort)0);

    var glyphIndices = new ushort[str.Length];
    var advanceWidths = new double[str.Length];

    for (int i = 0; i < str.Length; i++)
    {
        glyphIndices[i] = map.GetValueOrDefault(str[i], fallbackIndex);
        advanceWidths[i] = _cellWidth; // FIXED width — eliminates misalignment
    }

    double x = HorizontalPadding + startCol * _cellWidth;
    double baseline = y + _glyphTypeface.Baseline * _fontSize;

    var glyphRun = new GlyphRun(
        glyphTypeface: gt,
        bidiLevel: 0,
        isSideways: false,
        renderingEmSize: _fontSize,
        pixelsPerDip: (float)dpi,
        glyphIndices: glyphIndices,
        baselineOrigin: new Point(x, baseline),
        advanceWidths: advanceWidths,
        glyphOffsets: null,
        characters: null,
        deviceFontName: null,
        clusterMap: null,
        caretStops: null,
        language: null);

    var brush = dim
        ? GetCachedBrush(Color.FromArgb(128, fgColor.R, fgColor.G, fgColor.B))
        : GetCachedBrush(fgColor);

    dc.DrawGlyphRun(brush, glyphRun);

    // Decorations (unchanged)
    double runWidth = str.Length * _cellWidth;
    if (underline) { /* same pen drawing */ }
    if (strikethrough) { /* same pen drawing */ }
}
```

**Key advantage:** `advanceWidths[i] = _cellWidth` forces every character to exactly one cell width. No more misalignment from proportional spacing.

#### Fallback for missing glyphs:

Characters not in the primary font's `CharacterToGlyphMap` (emoji, rare Unicode) fall back to `FormattedText` for that specific run only. This is rare and doesn't affect performance for normal terminal output.

```csharp
bool hasMissingGlyph = false;
for (int i = 0; i < str.Length; i++)
{
    if (!map.ContainsKey(str[i])) { hasMissingGlyph = true; break; }
}
if (hasMissingGlyph)
{
    FlushTextRunFallback(dc, dpi, y, startCol, ...); // old FormattedText path
    return;
}
```

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

## Architecture Summary

```
Output arrives (4KB chunks)
  │
  ├─ TerminalSession.ReadLoop/FeedOutput
  │   ├─ Parser processes VT sequences → TerminalBuffer
  │   │   └─ SetChar/Scroll/etc mark affected rows dirty
  │   └─ Redraw?.Invoke() → TerminalControl.OnRedraw()
  │       └─ _needsRender = true  (atomic, no dispatch)
  │
  └─ CompositionTarget.Rendering (vsync, ~60fps)
      └─ if (_needsRender) Render()
          ├─ Draw background (full rect)
          ├─ For each visible row:
          │   ├─ Skip if !dirty && viewport hasn't moved
          │   ├─ Batch background rects (merge same-color)
          │   └─ Batch text into GlyphRun (same style)
          │       └─ Fixed advanceWidths = _cellWidth
          ├─ Draw cursor
          ├─ Draw scrollbar
          └─ buffer.ClearDirtyFlags()
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
