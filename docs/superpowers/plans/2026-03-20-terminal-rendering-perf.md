# Terminal Rendering Performance Overhaul — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace FormattedText with GlyphRun rendering + frame limiter + dirty-row caching for 60fps terminal performance and pixel-perfect character alignment.

**Architecture:** CompositionTarget.Rendering drives a vsync-synced render loop. TerminalBuffer tracks dirty rows. TerminalControl caches per-row GlyphRuns and replays them via DrawingContext, rebuilding only dirty rows. Background rects are batched per-row.

**Tech Stack:** WPF (DrawingVisual, GlyphRun, CompositionTarget.Rendering), .NET 10

**Spec:** `docs/superpowers/specs/2026-03-20-terminal-rendering-perf-design.md`

---

## Task 1: Frame Limiter (CompositionTarget.Rendering)

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`

- [ ] **Step 1: Replace `_renderQueued` with volatile flag**

Replace field at line 41:

```csharp
// REMOVE:
private int _renderQueued;
// ADD:
private volatile bool _needsRender;
```

- [ ] **Step 2: Simplify `RequestRender()`**

Replace lines 288-301:

```csharp
private void RequestRender(System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Background)
{
    _needsRender = true;
}
```

- [ ] **Step 3: Add CompositionTarget.Rendering handler + lifecycle**

Add after constructor `ClipToBounds = true` (line 153):

```csharp
Loaded += OnControlLoaded;
Unloaded += OnControlUnloaded;
```

Add methods:

```csharp
private void OnControlLoaded(object sender, RoutedEventArgs e)
{
    CompositionTarget.Rendering -= OnCompositionTargetRendering;
    CompositionTarget.Rendering += OnCompositionTargetRendering;
}

private void OnControlUnloaded(object sender, RoutedEventArgs e)
{
    CompositionTarget.Rendering -= OnCompositionTargetRendering;
}

private void OnCompositionTargetRendering(object? sender, EventArgs e)
{
    if (!_needsRender) return;
    _needsRender = false;
    Render();
}
```

- [ ] **Step 4: Update `AttachSession` direct `Render()` call**

At line 192, replace `Render()` with `_needsRender = true`.

- [ ] **Step 5: Build and verify**

Close cmux, build, run. Test with `seq 1 100000` — output should stream without UI freeze. Cursor blink, selection, search, scrollback should all work.

- [ ] **Step 6: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs
git commit -m "perf(terminal): replace Dispatcher.BeginInvoke with CompositionTarget.Rendering frame limiter"
```

---

## Task 2: Row-Level Dirty Tracking in TerminalBuffer

**Files:**

- Modify: `src/Cmux.Core/Terminal/TerminalBuffer.cs`

- [ ] **Step 1: Add dirty tracking fields and API**

Add fields after line 12:

```csharp
private bool[] _dirtyRows;
private bool _allDirty = true;
```

Add public API (near existing `MarkAllDirty()` at line 683):

```csharp
public bool IsRowDirty(int row) => _allDirty || (row >= 0 && row < Rows && _dirtyRows[row]);

public void MarkRowDirty(int row)
{
    if (row >= 0 && row < Rows) _dirtyRows[row] = true;
}

public new void MarkAllDirty()
{
    _allDirty = true;
}

public void ClearDirtyFlags()
{
    _allDirty = false;
    Array.Clear(_dirtyRows, 0, _dirtyRows.Length);
}
```

- [ ] **Step 2: Initialize in constructor and Resize**

In constructor (after `_cells` init at line 69):

```csharp
_dirtyRows = new bool[Rows];
```

In `Resize()` (after new `_cells` is assigned):

```csharp
_dirtyRows = new bool[newRows];
_allDirty = true;
```

- [ ] **Step 3: Mark dirty in all mutation methods**

Add `MarkRowDirty(row)` or `_allDirty = true` in each mutation method:

| Method                      | Add                                                     |
| --------------------------- | ------------------------------------------------------- |
| `SetChar(row, col, ...)`    | `MarkRowDirty(row);` after cell write                   |
| `WriteChar(c)`              | `MarkRowDirty(CursorRow);` after cell write at line 148 |
| `Clear()`                   | `_allDirty = true;`                                     |
| `ScrollUp(lines)`           | `_allDirty = true;` before `RaiseContentChanged()`      |
| `ScrollDown(lines)`         | `_allDirty = true;` before `RaiseContentChanged()`      |
| `EraseInDisplay(mode)`      | `_allDirty = true;` before `RaiseContentChanged()`      |
| `EraseInLine(mode)`         | `MarkRowDirty(CursorRow);`                              |
| `EraseChars(count)`         | `MarkRowDirty(CursorRow);`                              |
| `InsertLines(count)`        | `_allDirty = true;`                                     |
| `DeleteLines(count)`        | `_allDirty = true;`                                     |
| `InsertChars(count)`        | `MarkRowDirty(CursorRow);`                              |
| `DeleteChars(count)`        | `MarkRowDirty(CursorRow);`                              |
| `SwitchToAlternateScreen()` | `_allDirty = true;`                                     |
| `SwitchToMainScreen()`      | `_allDirty = true;`                                     |

- [ ] **Step 4: Build and verify**

No visible effect yet (renderer doesn't read flags). Verify app compiles and runs correctly.

- [ ] **Step 5: Commit**

```bash
git add src/Cmux.Core/Terminal/TerminalBuffer.cs
git commit -m "perf(terminal): add row-level dirty tracking to TerminalBuffer"
```

---

## Task 3: GlyphRun Rendering + Row Cache

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`
- Modify: `src/Cmux.Core/Terminal/TerminalSession.cs` (1 line)

- [ ] **Step 1: Expose RenderLock on TerminalSession**

Add to `TerminalSession.cs`:

```csharp
public object RenderLock => _lock;
```

- [ ] **Step 2: Add GlyphTypeface fields and initialization**

Add fields after line 72 in TerminalControl.cs:

```csharp
private GlyphTypeface? _glyphTypeface;
private GlyphTypeface? _glyphTypefaceBold;
private GlyphTypeface? _glyphTypefaceItalic;
private GlyphTypeface? _glyphTypefaceBoldItalic;
private readonly Dictionary<int, ushort> _glyphCache = new();
private readonly Dictionary<int, ushort> _glyphCacheBold = new();
private readonly Dictionary<int, ushort> _glyphCacheItalic = new();
private readonly Dictionary<int, ushort> _glyphCacheBoldItalic = new();
```

Add init + helper methods:

```csharp
private void InitGlyphTypefaces()
{
    _glyphTypeface = null;
    _glyphTypefaceBold = null;
    _glyphTypefaceItalic = null;
    _glyphTypefaceBoldItalic = null;
    _glyphCache.Clear();
    _glyphCacheBold.Clear();
    _glyphCacheItalic.Clear();
    _glyphCacheBoldItalic.Clear();
    _typeface.TryGetGlyphTypeface(out _glyphTypeface);
}

private GlyphTypeface? ResolveGlyphTypeface(bool bold, bool italic)
{
    if (!bold && !italic) return _glyphTypeface;
    if (bold && !italic)
    {
        _glyphTypefaceBold ??= GetTypeface(true, false).Let(tf => { tf.TryGetGlyphTypeface(out var gt); return gt; });
        // Simpler: use TryGetGlyphTypeface directly
        if (_glyphTypefaceBold == null)
            GetTypeface(true, false).TryGetGlyphTypeface(out _glyphTypefaceBold);
        return _glyphTypefaceBold;
    }
    if (!bold && italic)
    {
        if (_glyphTypefaceItalic == null)
            GetTypeface(false, true).TryGetGlyphTypeface(out _glyphTypefaceItalic);
        return _glyphTypefaceItalic;
    }
    if (_glyphTypefaceBoldItalic == null)
        GetTypeface(true, true).TryGetGlyphTypeface(out _glyphTypefaceBoldItalic);
    return _glyphTypefaceBoldItalic;
}

private Dictionary<int, ushort> GetGlyphCache(bool bold, bool italic) =>
    (bold, italic) switch
    {
        (false, false) => _glyphCache,
        (true, false) => _glyphCacheBold,
        (false, true) => _glyphCacheItalic,
        _ => _glyphCacheBoldItalic,
    };

private static ushort LookupGlyph(GlyphTypeface gt, Dictionary<int, ushort> cache, int codepoint)
{
    if (cache.TryGetValue(codepoint, out var idx)) return idx;
    if (gt.CharacterToGlyphMap.TryGetValue(codepoint, out idx))
    {
        cache[codepoint] = idx;
        return idx;
    }
    return 0;
}
```

Call `InitGlyphTypefaces()` after `CalculateCellSize()` in constructor.

- [ ] **Step 3: Add CachedRow struct and row cache**

```csharp
private struct CachedRow
{
    public List<(GlyphRun Run, SolidColorBrush Brush)>? GlyphRuns;
    public List<(Rect Rect, SolidColorBrush Brush)>? Backgrounds;
    public List<(Point From, Point To, Pen Pen)>? Decorations;
}
private CachedRow[]? _rowCache;
private int _lastViewStartLine = -1;
```

Invalidate in `InvalidateRenderCaches()` and when `_rows`/`_cols` change in `CalculateTerminalSize()`.

- [ ] **Step 4: Write `BuildRowCache()` method**

Port the current column loop (lines 435-539) into `BuildRowCache()`. Same per-cell logic (color resolution, inverse, selection, search highlights), but output goes to cache lists instead of DrawingContext. Text runs build GlyphRun objects using `LookupGlyph()` with fixed `advanceWidths = cell.Width * _cellWidth`.

For missing glyphs (index 0): split the run and use single-char FormattedText fallback constrained to `_cellWidth`.

Baseline: `y + gt.Baseline * _fontSize`.

- [ ] **Step 5: Rewrite `Render()` with lock + dirty-row check + cache replay**

```csharp
private void Render()
{
    if (_session == null) return;
    try
    {
        var buffer = _session.Buffer;
        using var dc = _visual.RenderOpen();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Background fill
        dc.DrawRectangle(GetCachedBrush(ToWpfColor(_theme.Background)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        // Notification ring, focus indicator (unchanged)
        // ...

        int scrollbackCount = buffer.ScrollbackCount;
        int viewStartLine = scrollbackCount + _scrollOffset;

        if (viewStartLine != _lastViewStartLine)
        {
            _rowCache = null;
            _lastViewStartLine = viewStartLine;
        }

        if (_rowCache == null || _rowCache.Length != _rows)
            _rowCache = new CachedRow[_rows];

        var searchMatchSet = _searchMatchSetCache ?? EmptyMatchSet;
        var currentMatchSet = _currentMatchSetCache ?? EmptyMatchSet;
        // ... search brushes ...

        lock (_session.RenderLock)
        {
            for (int visRow = 0; visRow < _rows; visRow++)
            {
                int virtualLine = viewStartLine + visRow;
                int bufferRow = virtualLine - scrollbackCount;
                bool isLiveRow = bufferRow >= 0 && bufferRow < buffer.Rows;

                bool needsRebuild = _rowCache[visRow].GlyphRuns == null
                    || (isLiveRow && buffer.IsRowDirty(bufferRow));

                if (needsRebuild)
                    BuildRowCache(ref _rowCache[visRow], visRow, buffer,
                        virtualLine, scrollbackCount, dpi,
                        searchMatchSet, currentMatchSet, ...);
            }
            buffer.ClearDirtyFlags();
        }

        // Replay all cached rows
        for (int visRow = 0; visRow < _rows; visRow++)
        {
            ref var cache = ref _rowCache[visRow];
            if (cache.Backgrounds != null)
                foreach (var (rect, brush) in cache.Backgrounds)
                    dc.DrawRectangle(brush, null, rect);
            if (cache.GlyphRuns != null)
                foreach (var (run, brush) in cache.GlyphRuns)
                    dc.DrawGlyphRun(brush, run);
            if (cache.Decorations != null)
                foreach (var (from, to, pen) in cache.Decorations)
                    dc.DrawLine(pen, from, to);
        }

        // Cursor (unchanged, outside lock)
        // Scrollbar (unchanged)
    }
    catch (Exception ex) { Debug.WriteLine($"[TerminalControl] Render failed: {ex}"); }
}
```

- [ ] **Step 6: Invalidate cache on selection/search changes**

In `SelectionChanged` handler, `SetSearchHighlights()`, `ClearSearchHighlights()`: set `_rowCache = null`.

- [ ] **Step 7: Build and verify**

Close cmux, build, run. Test:

1. `seq 1 100000` — smooth 60fps
2. Characters `≈ │ ─ ┌ ┐ └ ┘ █ ▓` — grid-aligned, no offset
3. `htop` or `vim` — colors and backgrounds correct
4. Selection, search highlights, cursor blink, scrollback all working
5. Split panes — multiple terminals rendering simultaneously

- [ ] **Step 8: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs src/Cmux.Core/Terminal/TerminalSession.cs
git commit -m "perf(terminal): replace FormattedText with GlyphRun rendering and row cache"
```

---

## Task 4: Background Rectangle Batching + Pen Cache

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`

- [ ] **Step 1: Add pen cache**

```csharp
private readonly Dictionary<Color, Pen> _penCache = new();

private Pen GetCachedPen(Color color, double thickness = 1)
{
    if (_penCache.TryGetValue(color, out var pen)) return pen;
    pen = new Pen(GetCachedBrush(color), thickness);
    pen.Freeze();
    _penCache[color] = pen;
    return pen;
}
```

Clear `_penCache` in `InvalidateRenderCaches()`.

- [ ] **Step 2: Add background batching in `BuildRowCache()`**

Replace per-cell `DrawRectangle` with batching:

```csharp
// Track current background run
Color currentBgColor = default;
double bgRunX = 0, bgRunWidth = 0;
bool hasBgRun = false;

// In column loop, instead of adding rect per cell:
var wpfBg = ToWpfColor(cellBg);
if (!cellBg.IsDefault)
{
    if (hasBgRun && wpfBg == currentBgColor)
        bgRunWidth += ceilW;
    else
    {
        if (hasBgRun)
            cache.Backgrounds!.Add((new Rect(bgRunX, y, bgRunWidth, ceilH), GetCachedBrush(currentBgColor)));
        currentBgColor = wpfBg;
        bgRunX = x;
        bgRunWidth = ceilW;
        hasBgRun = true;
    }
}
else if (hasBgRun)
{
    cache.Backgrounds!.Add((new Rect(bgRunX, y, bgRunWidth, ceilH), GetCachedBrush(currentBgColor)));
    hasBgRun = false;
}

// After column loop:
if (hasBgRun)
    cache.Backgrounds!.Add((new Rect(bgRunX, y, bgRunWidth, ceilH), GetCachedBrush(currentBgColor)));
```

- [ ] **Step 3: Replace per-frame Pen allocations**

Replace `new Pen(...)` in notification ring, focus indicator, URL hover with `GetCachedPen()`.

- [ ] **Step 4: Build and verify**

Close cmux, build, run. Verify colored backgrounds (vim, htop) render without gaps. `seq 1 100000` still smooth.

- [ ] **Step 5: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs
git commit -m "perf(terminal): batch background rects and cache pen objects"
```

---

## Risks and Mitigations

| Risk                                                 | Mitigation                                                                  |
| ---------------------------------------------------- | --------------------------------------------------------------------------- |
| `TryGetGlyphTypeface()` returns false for some fonts | Fall back to FormattedText for the entire row when `_glyphTypeface` is null |
| Lock contention between read thread and render       | Lock held <1ms per frame with dirty-row skipping; negligible                |
| Row cache invalidation edge cases (bell, URL hover)  | Use `_rowCache = null` for infrequent events; optimize later if needed      |
| Scrollback rows have no dirty flags                  | Handled by `_lastViewStartLine` viewport change detection                   |
