# cmux-windows Custom Adaptations — Design Spec

## Overview

Customize cmux-windows for a developer workflow centered on multiple Expo/React Native projects with heavy Claude Code usage, a macOS VM for iOS builds, and PowerShell as primary shell.

**Approach**: Fork with isolated extension layer. Custom code lives in dedicated files/classes that integrate with existing services via events and interfaces. Minimal modifications to existing files to ease upstream merges.

---

## Feature 1+5: Workspace Templates + Auto-cd

### Problem

Creating a workspace for each project requires manual steps: create workspace, open pane, cd to directory, launch Claude Code. This should be one click.

### Design

**New file**: `Cmux.Core/Services/WorkspaceTemplateService.cs`

**Data model** — `WorkspaceTemplate`:

```csharp
public class WorkspaceTemplate
{
    public string Name { get; set; }          // e.g. "VTC-Planner"
    public string Directory { get; set; }     // e.g. "C:/Users/TRINITX/Desktop/VTC-Planner"
    public string? StartupCommand { get; set; } // e.g. "claude --dangerously-skip-permissions --effort max"
    public string? IconGlyph { get; set; }
    public string? AccentColor { get; set; }
}
```

**Templates are stored in `settings.json`** via `CmuxSettings.WorkspaceTemplates` (a `List<WorkspaceTemplate>`). Shipped with predefined defaults on first run:

- VTC-Planner → `C:/Users/TRINITX/Desktop/VTC-Planner`
- FidelyPass → `C:/Users/TRINITX/Desktop/FidelyPass`
- Qwitt → `C:/Users/TRINITX/Desktop/Qwitt`
- dress_up → `C:/Users/TRINITX/Desktop/dress_up`
- Email verifier → `C:/Users/TRINITX/Desktop/Email verifier`
- Scraping → `C:/Users/TRINITX/Desktop/Scraping`

All templates use startup command: `claude --dangerously-skip-permissions --effort max`

> **Note**: Templates do NOT use `--worktree` because they open a single pane in the project root. The `--worktree` flag is only used by layout buttons (Feature 10) which open parallel Claude sessions that need isolation.

**Behavior**:

1. Button "New from template" in sidebar (next to existing "+" button) → dropdown with template list
2. Creates workspace with project name, icon, and accent color
3. Opens a pane that `cd`s into the project directory
4. Automatically runs the startup command

**UI**: Dropdown button in sidebar. Each item shows project name + icon.

### Feature 17: New Workspace → Folder Picker

When clicking the standard "New workspace" button (not template), open a Windows folder picker to let the user choose a directory. The workspace name defaults to the folder name. The pane auto-cds into that directory.

**Implementation**: `Microsoft.Win32.OpenFolderDialog` (available in .NET 8+ and confirmed in .NET 10). No WinForms reference needed.

---

## Feature 2+6: Claude Code Detection + Status Indicators

### Problem

When running multiple Claude Code sessions in parallel, there's no way to see which ones are working, waiting for input, or idle without switching to each pane.

### Design

**New file**: `Cmux.Core/Services/ClaudeCodeStatusService.cs`

**New file**: `Cmux.Core/Models/ClaudeStatus.cs` — contains only the enum, referenced by the service and ViewModels.

```csharp
public enum ClaudeStatus
{
    Idle,            // No claude process detected
    Working,         // claude process + output received < 3s ago
    WaitingForInput  // claude process + notification/BEL received + output stopped
}
```

**Detection mechanism** — 3 combined signals:

| Signal                                      | Source                                                             | Meaning                            |
| ------------------------------------------- | ------------------------------------------------------------------ | ---------------------------------- |
| Process `claude` in process tree            | `AgentDetector.DetectFromProcessId(shellPid)`                      | Claude is running                  |
| PTY output flowing                          | `RawOutputReceived` event timestamp                                | Claude is actively working         |
| Notification received (OSC 9/99/777) or BEL | `OscHandler.NotificationReceived` / `TerminalSession.BellReceived` | Claude finished, waiting for input |

**Shell PID source**: `TerminalSession` exposes the shell process ID via `TerminalProcess.ProcessId`. The service obtains this from each pane's `TerminalSession` instance.

**Service behavior**:

- Timer polls `AgentDetector.DetectFromProcessId()` every ~2 seconds
- **Performance**: Use a single batched WMI query (`SELECT Name, ParentProcessId FROM Win32_Process WHERE ParentProcessId IN (pid1, pid2, ...)`) instead of one query per pane to avoid CPU spikes with 4+ panes
- Subscribes to `RawOutputReceived` to track `lastOutputTime` per pane
- Subscribes to `NotificationReceived` and `BellReceived` for completion detection
- Exposes `Dictionary<Guid, ClaudeStatus>` (paneId → status)
- Fires `StatusChanged(Guid paneId, ClaudeStatus oldStatus, ClaudeStatus newStatus)` event
- **Cleanup**: Unsubscribes from events when a pane is closed/disposed

**State transition rules**:

| From                      | To                                                                     | Condition |
| ------------------------- | ---------------------------------------------------------------------- | --------- |
| Idle → Working            | Claude process detected + first output received                        |
| Working → WaitingForInput | BEL or OSC notification received + no output for 2s after notification |
| WaitingForInput → Working | New output burst detected (user sent a new prompt)                     |
| Working → Idle            | Claude process disappears from process tree                            |
| WaitingForInput → Idle    | Claude process disappears from process tree                            |

**Debounce**: 2s quiet period after BEL/notification before transitioning to WaitingForInput. This prevents false transitions when Claude emits a BEL then continues briefly with output.

**UI — Dot on tab** (`Cmux/Controls/SurfaceTabBar.xaml` + `SurfaceTabBar.xaml.cs`):

- Green pulsing dot = Working
- Orange dot = WaitingForInput
- Hidden = Idle

**UI — Status bar in pane** (`Cmux/Controls/TerminalControl.cs`):

- Thin bar at bottom of pane: "Claude Code working..." (green) / "Claude Code waiting for input" (orange)
- Bar disappears when Idle

**Integration points**:

- `SurfaceTabBar.xaml` + `.xaml.cs` — add colored dot, subscribe to `StatusChanged`
- `Cmux/Controls/TerminalControl.cs` — add status bar overlay at bottom
- `Cmux/ViewModels/SurfaceViewModel.cs` — expose `ClaudeStatus` per pane for XAML binding

---

## Feature 8: Toolbar Buttons — SSH Mac + PowerShell

### Problem

Frequently need to SSH to macOS VM for iOS builds and open PowerShell in current directory.

### Design

**Two new buttons in toolbar** (after the layout buttons section, with a separator):

| Button     | Icon                                   | Tooltip               | Action                                      |
| ---------- | -------------------------------------- | --------------------- | ------------------------------------------- |
| SSH Mac    | `` (Apple glyph, Segoe MDL2 or custom) | "SSH to macOS VM"     | New pane → `ssh trinitx@192.168.1.32`       |
| PowerShell | `>_` (terminal glyph `\uE756`)         | "New PowerShell pane" | New pane → PowerShell in cwd of active pane |

**SSH config** in `CmuxSettings.cs`:

```csharp
public string SshHost { get; set; } = "trinitx@192.168.1.32";
public string SshKeyPath { get; set; } = "~/.ssh/id_ed25519";
```

**MainWindow.xaml**: Add two buttons after existing layout section with a separator.

**MainWindow.xaml.cs**: New handlers `ToolbarSshMac_Click` and `ToolbarPowerShell_Click`.

---

## Feature 10: Layout Buttons Launch Claude Code

### Problem

The 3 layout buttons (2-col, grid, main+stack) create empty panes. For this workflow, each pane should automatically launch Claude Code.

### Design

**Modify existing handlers** in `MainWindow.xaml.cs`:

- `ToolbarLayout2Col_Click` — after creating 2-column layout, each pane runs the startup command
- `ToolbarLayoutGrid_Click` — same for 4 panes in grid
- `ToolbarLayoutMainStack_Click` — same for main+stack panes

**Implementation**: After the existing layout creation logic, iterate over all leaf panes in the split tree via `SplitNode.GetLeaves()` and send the startup command to each `TerminalSession`.

**Command**: `claude --dangerously-skip-permissions --effort max --worktree`

> **Note on `--worktree`**: This flag creates isolated git worktrees for each Claude session, which is the intended behavior when running parallel Claude instances on the same repo. If the workspace is not a git repo, Claude Code handles this gracefully (falls back to normal mode). This is intentional and desired.

---

## Feature 13: Browser Auto-Open on Localhost

### Problem

When a dev server starts (Expo, Next.js, etc.), you have to manually open the browser to see the app. The browser should open automatically.

### Design

**New file**: `Cmux.Core/Services/PortDetectionService.cs`

**Detection mechanism**:

- Subscribe to `RawOutputReceived` on each pane's `TerminalSession`
- Regex scan output for HTTP URLs:
  - `https?://localhost:\d+`
  - `https?://127\.0\.0\.1:\d+`
  - `Local:\s+https?://localhost:\d+` (Vite, Next.js output format)
- **Expo `exp://` URLs are ignored** — only HTTP/HTTPS URLs trigger the browser. Expo dev servers also print an `http://localhost:8081` URL which will be caught.
- When detected, fire `DevServerStarted(Guid paneId, string url)` event

**Behavior**:

1. Dev server URL detected in terminal output
2. Auto-open browser split pane (using existing `BrowserControl`) with that URL
3. Only trigger once per unique URL per pane (avoid re-triggering on restart output)
4. Track detected URLs in a `HashSet<string>` per pane

**Cleanup**: Unsubscribe from `RawOutputReceived` and clear tracked URLs when a pane is closed/disposed.

**Setting** in `CmuxSettings.cs`:

```csharp
public bool AutoOpenBrowserOnDevServer { get; set; } = true;
```

---

## Feature 14: Flash Focused Panel

### Problem

With multiple panes, it's hard to tell which one just received focus after switching.

### Design

**Modify**: `Cmux/Controls/SplitPaneContainer.cs`

**Behavior**: When a pane receives focus, briefly flash its border:

- Border color = workspace accent color (or default accent)
- Animation: opacity 0 → 1 → 0 over ~400ms (single pulse)
- Uses WPF `Storyboard` with `DoubleAnimation` on `Border.Opacity`

**Implementation**:

- Listen to `FocusedPaneId` property changes in `SplitPaneContainer` (already tracked)
- Add a `Border` overlay element around each pane
- Trigger animation when `FocusedPaneId` changes

**No setting needed** — always on, lightweight.

---

## Feature 16: Right-Click → "Open in Windows Explorer"

### Problem

No quick way to open the current terminal directory in Windows Explorer.

### Design

**Modify**: `Cmux/Controls/TerminalControl.cs` — add item to existing context menu (already created at line ~1321)

**Behavior**:

1. Right-click on terminal → existing context menu shows additional "Open in Windows Explorer" item
2. Click → `Process.Start("explorer.exe", cwd)` where `cwd` is the pane's current working directory (tracked by `OscHandler.WorkingDirectoryChanged`)

**Implementation**:

- Add `MenuItem` to the existing `ContextMenu` in `TerminalControl.cs`
- Use `TerminalSession.WorkingDirectory` property for the path
- Fallback: if cwd is unknown, use workspace directory

---

## Files to Create

| File                                             | Purpose                                            |
| ------------------------------------------------ | -------------------------------------------------- |
| `Cmux.Core/Services/WorkspaceTemplateService.cs` | Template definitions and creation logic            |
| `Cmux.Core/Services/ClaudeCodeStatusService.cs`  | Agent status detection (process + output + OSC)    |
| `Cmux.Core/Services/PortDetectionService.cs`     | Dev server URL detection in terminal output        |
| `Cmux.Core/Models/WorkspaceTemplate.cs`          | Template data model                                |
| `Cmux.Core/Models/ClaudeStatus.cs`               | Status enum (referenced by service and ViewModels) |

## Files to Modify

| File                                  | Changes                                                                                                                                                                                       |
| ------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Cmux/Views/MainWindow.xaml`          | Add SSH Mac + PowerShell buttons with separator, template dropdown in sidebar                                                                                                                 |
| `Cmux/Views/MainWindow.xaml.cs`       | Add `ToolbarSshMac_Click`, `ToolbarPowerShell_Click` handlers; modify `ToolbarLayout2Col_Click`, `ToolbarLayoutGrid_Click`, `ToolbarLayoutMainStack_Click` to launch Claude Code in each pane |
| `Cmux/Controls/SurfaceTabBar.xaml`    | Add colored status dot element next to tab name                                                                                                                                               |
| `Cmux/Controls/SurfaceTabBar.xaml.cs` | Subscribe to `ClaudeCodeStatusService.StatusChanged`, update dot color/visibility                                                                                                             |
| `Cmux/Controls/SplitPaneContainer.cs` | Add Border overlay per pane, animate on `FocusedPaneId` change                                                                                                                                |
| `Cmux/Controls/TerminalControl.cs`    | Add status bar overlay at bottom, add "Open in Windows Explorer" to existing context menu                                                                                                     |
| `Cmux/ViewModels/SurfaceViewModel.cs` | Expose `ClaudeStatus` per pane for XAML data binding                                                                                                                                          |
| `Cmux.Core/Config/CmuxSettings.cs`    | Add `SshHost`, `SshKeyPath`, `AutoOpenBrowserOnDevServer`, `WorkspaceTemplates` list                                                                                                          |
| `Cmux/App.xaml.cs`                    | Initialize `ClaudeCodeStatusService`, `PortDetectionService`, `WorkspaceTemplateService` on startup                                                                                           |

## Out of Scope

- Sound notification on agent complete (handled by existing Claude Code hook)
- Find in terminal (already exists: Ctrl+Shift+F)
- Browser base features (already exists: BrowserControl with WebView2)
- Command log / Session vault modifications (existing behavior is sufficient)
- Auto-update mechanism
- GPU-accelerated rendering
