# WriteableBitmap Migration — Eliminate HwndHost DWM Flickering

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the HwndHost + DXGI swap chain presentation with offscreen D3D11 rendering + WriteableBitmap to eliminate full-screen DWM brightness flickering on NVIDIA GPUs.

**Architecture:** D3D11 renders to an offscreen texture (not a swap chain). After each frame, the texture is copied to a CPU-readable staging texture, mapped, and blitted into a WPF WriteableBitmap displayed via an Image control. This eliminates the child HWND, the swap chain, and all DWM airspace composition conflicts. Mouse/keyboard input uses native WPF routing instead of Win32 message forwarding.

**Tech Stack:** D3D11 (Vortice), WPF WriteableBitmap, .NET 10

---

## File Structure

| File                                        | Action                   | Responsibility                                                                                                                   |
| ------------------------------------------- | ------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cmux/Rendering/D3DTerminalRenderer.cs` | Modify                   | Replace swap chain with offscreen RT + staging texture. New `CopyFrameToBuffer()` method. Remove HWND param from `Initialize()`. |
| `src/Cmux/Controls/TerminalControl.cs`      | Modify                   | Replace D3DRenderHost with Image + WriteableBitmap. Remove RawInput forwarding. Use native WPF mouse input.                      |
| `src/Cmux/Controls/D3DRenderHost.cs`        | Keep (mouse cursor only) | Keep only for setting the mouse cursor via WNDCLASSEX. May delete later if unneeded.                                             |

---

## Task 1: Replace swap chain with offscreen render target in D3DTerminalRenderer

**Files:**

- Modify: `src/Cmux/Rendering/D3DTerminalRenderer.cs`

### Changes

Replace these fields:

```csharp
// REMOVE:
private IDXGISwapChain1? _swapChain;
private bool _tearingSupported;

// ADD:
private ID3D11Texture2D? _offscreenTarget;
private ID3D11Texture2D? _stagingTexture;
```

#### Step-by-step:

- [ ] **Step 1: Change Initialize() — remove HWND, create offscreen textures instead of swap chain**

Remove the `nint hwnd` parameter. Remove swap chain creation (lines 115-135). Replace with:

```csharp
// Create offscreen render target (replaces swap chain back buffer)
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
_offscreenTarget = _device.CreateTexture2D(rtDesc);
_rtv = _device.CreateRenderTargetView(_offscreenTarget);

// Create staging texture for CPU readback
var stagingDesc = rtDesc;
stagingDesc.Usage = ResourceUsage.Staging;
stagingDesc.BindFlags = BindFlags.None;
stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
_stagingTexture = _device.CreateTexture2D(stagingDesc);
```

- [ ] **Step 2: Replace CreateRenderTarget() — use offscreen texture instead of swap chain back buffer**

```csharp
private void CreateRenderTarget()
{
    _rtv?.Dispose();
    _rtv = _device!.CreateRenderTargetView(_offscreenTarget!);
}
```

- [ ] **Step 3: Replace Present() with CopyFrameToBuffer() at end of Render()**

Remove the entire Present block (lines 374-393). Replace with a new public method that copies the rendered frame to a caller-provided buffer:

```csharp
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
```

And in Render(), replace the Present block with just:

```csharp
// Frame is ready in _offscreenTarget — caller will call CopyFrameToBuffer()
```

- [ ] **Step 4: Update ResizeSwapChain → ResizeRenderTarget**

Rename method. Replace swap chain resize with texture recreation:

```csharp
public void ResizeRenderTarget(int pixelWidth, int pixelHeight,
    int cols, int rows, float cellWidth, float cellHeight)
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
```

- [ ] **Step 5: Update Dispose() — replace swap chain cleanup with texture cleanup**

```csharp
// Replace _swapChain?.Dispose() with:
_stagingTexture?.Dispose();
_offscreenTarget?.Dispose();
```

- [ ] **Step 6: Remove null check for \_swapChain in Render()**

In the guard at the top of Render(), replace `_swapChain == null` with `_offscreenTarget == null`.

- [ ] **Step 7: Build and fix compilation errors**

Run: `dotnet build src/Cmux/Cmux.csproj`

---

## Task 2: Replace D3DRenderHost with Image + WriteableBitmap in TerminalControl

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs`

### Changes

#### Step-by-step:

- [ ] **Step 1: Replace fields — D3DRenderHost → Image + WriteableBitmap**

```csharp
// REMOVE:
private D3DRenderHost? _renderHost;
private Point? _rawMousePosition;

// ADD:
private System.Windows.Controls.Image? _renderImage;
private System.Windows.Media.Imaging.WriteableBitmap? _bitmap;
```

- [ ] **Step 2: Update constructor — create Image instead of D3DRenderHost**

Replace `_renderHost = new D3DRenderHost();` with:

```csharp
_renderImage = new System.Windows.Controls.Image
{
    Stretch = System.Windows.Media.Stretch.None,
    HorizontalAlignment = HorizontalAlignment.Left,
    VerticalAlignment = VerticalAlignment.Top,
};
```

- [ ] **Step 3: Update OnControlLoaded — add Image to visual tree**

Replace the `_renderHost` block with:

```csharp
if (_renderImage != null && !IsAncestorOf(_renderImage))
{
    AddVisualChild(_renderImage);
    AddLogicalChild(_renderImage);
}
```

Remove `_renderHost.RawInput += OnRenderHostRawInput;` subscription.

- [ ] **Step 4: Update InitializeGpuRenderer — no more HWND**

Remove the `_renderHost!.Hwnd` parameter. Change:

```csharp
_gpuRenderer.Initialize(
    pw, ph,  // no HWND
    _theme.FontFamily, (float)_fontSize, (float)dpi,
    _cols, _rows, (float)_cellWidth, (float)_cellHeight,
    _theme);
```

Also change the init guard from `_renderHost?.Hwnd != nint.Zero` to just check dimensions:

```csharp
if (!_gpuInitialized && _renderImage != null)
```

- [ ] **Step 5: Update OnCompositionTargetRendering — blit to WriteableBitmap after Render()**

After `_gpuRenderer.Render(...)`, add the WriteableBitmap blit:

```csharp
// Copy rendered frame to WriteableBitmap
int pw = _gpuRenderer.PixelWidth;
int ph = _gpuRenderer.PixelHeight;
if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
{
    _bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
        pw, ph, 96 * dpi, 96 * dpi,
        System.Windows.Media.PixelFormats.Bgra32, null);
    _renderImage!.Source = _bitmap;
}

_bitmap.Lock();
try
{
    var buffer = new Span<byte>((void*)_bitmap.BackBuffer, pw * ph * 4);
    if (_gpuRenderer.CopyFrameToBuffer(buffer))
        _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, pw, ph));
}
finally
{
    _bitmap.Unlock();
}
```

- [ ] **Step 6: Update resize calls — ResizeSwapChain → ResizeRenderTarget**

Find-and-replace all `ResizeSwapChain(` calls with `ResizeRenderTarget(`.

- [ ] **Step 7: Update MeasureOverride/ArrangeOverride**

Replace `_renderHost` with `_renderImage`:

```csharp
protected override Size MeasureOverride(Size availableSize)
{
    _renderImage?.Measure(availableSize);
    return availableSize;
}

protected override Size ArrangeOverride(Size finalSize)
{
    _renderImage?.Arrange(new Rect(finalSize));
    // ... existing CalculateTerminalSize + resize logic ...
    return finalSize;
}
```

- [ ] **Step 8: Update VisualChildrenCount and GetVisualChild**

```csharp
protected override int VisualChildrenCount => _renderImage != null ? 1 : 0;

protected override Visual GetVisualChild(int index)
{
    if (index == 0 && _renderImage != null) return _renderImage;
    throw new ArgumentOutOfRangeException(nameof(index));
}
```

- [ ] **Step 9: Remove OnRenderHostRawInput and raw mouse position logic**

Delete the entire `OnRenderHostRawInput` method. Update `GetMousePos()` to always use the WPF fallback path (GetCursorPos + PointToScreen). Remove `_rawMousePosition` field.

- [ ] **Step 10: Update OnControlUnloaded — clean up Image instead of RenderHost**

Remove `_renderHost` references. The Image doesn't need special cleanup.

- [ ] **Step 11: Build and fix all compilation errors**

Run: `dotnet build src/Cmux/Cmux.csproj`
Fix any remaining references to `_renderHost`, `_swapChain`, `ResizeSwapChain`, `OnRenderHostRawInput`, `_rawMousePosition`.

- [ ] **Step 12: Commit**

```
feat(render): migrate from HwndHost swap chain to WriteableBitmap presentation

Eliminates the DXGI flip-model swap chain presenting to a child HWND
inside the WPF window, which caused full-screen DWM brightness flickering
on NVIDIA GPUs. The D3D11 renderer now draws to an offscreen texture and
copies the result to a WPF WriteableBitmap, fully integrated with WPF's
compositor. Mouse/keyboard input uses native WPF routing.
```

---

## Testing Checklist

After implementation, verify:

- [ ] Terminal text renders correctly (no visual regression)
- [ ] Cursor blinks normally
- [ ] Mouse selection works (click, drag, double-click word, triple-click line)
- [ ] Mouse wheel scrolling works
- [ ] URL detection (Ctrl+hover) works
- [ ] Resize works smoothly (drag window edge)
- [ ] No brightness flickering during long Claude Code output
- [ ] No brightness flickering during fast scrolling through scrollback
- [ ] Tab switching between terminals works
- [ ] Split panes render correctly
- [ ] Performance is acceptable (~60fps scrolling on RTX 3090)
- [ ] DPI change (move to different monitor) works
