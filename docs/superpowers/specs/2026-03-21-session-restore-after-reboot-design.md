# Session Restore After Reboot — Design Spec

## Problem

When cmux is closed and reopened (without PC reboot), the daemon keeps all ConPTY processes alive. Reconnection is seamless — the user sees the exact same terminal state.

After a PC reboot, all processes are killed. Currently:

- Tab layout and pane structure are restored from `session.json` (works)
- Buffer snapshots are displayed (works)
- Claude Code sessions are NOT relaunched automatically (broken)
- Shell command history is lost (not implemented)

**Goal:** Make the post-reboot experience indistinguishable from the post-close experience. The user should see their terminals exactly as they left them, with Claude Code sessions resuming and shell history available via arrow keys.

## Root Causes

1. **Daemon auto-start not registered** — `RegisterDaemonAutoStart()` calls `FindDaemonExecutable()` which returns `null`, so the registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CmuxDaemon` is never written. After reboot, the daemon is not running when cmux starts.

2. **RelaunchClaudeCodePanes timing issue** — Uses a fixed 1.5s delay (`Task.Delay(1500)`) before sending `claude --resume`. After a reboot (Phase 2: cmux starts daemon itself), the shell is not ready in 1.5s. The command is sent to a shell that hasn't printed its prompt yet.

3. **No Windows shutdown hook** — Auto-save runs every 10s via `DispatcherTimer`. If Windows kills cmux during shutdown, the last snapshot could be up to 10s stale. `OnClosing()` may not fire during a system shutdown.

4. **Shell history not injected** — `CommandHistory` is saved in `session.json` (last 500 commands per pane) but only used for snapshot display. It is never written to the shell's HISTFILE on restore.

## Solution: Approach B — Client-Side Transparent Restoration

All changes are in cmux client code. The daemon is NOT modified. The happy path (daemon alive, no reboot) is completely unchanged.

### Change 1: Fix Daemon Auto-Start

**File:** `src/Cmux.Core/IPC/DaemonClient.cs`

**Problem:** `FindDaemonExecutable()` (line 133) returns `null`, preventing registry registration. The method searches two locations:

1. Next to the current executable (`AppContext.BaseDirectory + "cmux-daemon.exe"`) — for deployed/published scenarios
2. In sibling project build output (`src/Cmux.Daemon/bin/Debug/...`) — for dev builds

In the current dev setup, the daemon is likely not built or the path traversal to `src/` fails because `AppContext.BaseDirectory` doesn't contain a `src` ancestor directory.

**Fix:** During implementation, check the daemon debug log (`%LOCALAPPDATA%\cmux\daemon-debug.log`) for the `[FindDaemon]` entries to identify the exact failure. Likely fixes:

- Build the daemon project (`dotnet build src/Cmux.Daemon`)
- Or fix the path traversal logic if `AppContext.BaseDirectory` has an unexpected structure

Once fixed, `RegisterDaemonAutoStart()` writes to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and Windows launches the daemon at login.

**Fallback preserved:** If the daemon is still not running at cmux startup (registry cleared, binary moved), cmux starts it itself (Phase 2 — existing behavior).

**Benefit:** After reboot, the daemon is already running when cmux starts. This means Phase 1 connects to an existing daemon (fast, reliable) instead of Phase 2 (start daemon, wait, connect — slower, timing-sensitive).

### Change 2: Windows Shutdown Hook

**File:** `src/Cmux/Views/MainWindow.xaml.cs`

**What:** Intercept `WM_QUERYENDSESSION` and `WM_ENDSESSION` in the **existing** `WndProc` method (line 77).

**Action:** On either message, call `ViewModel.SaveSession()` immediately with current window geometry. This guarantees `session.json` contains the freshest possible state before Windows kills the process. A `_sessionSavedForShutdown` flag prevents double-save if both messages fire.

**Implementation:** Extend the existing `WndProc` (do NOT create a second one):

```csharp
private bool _sessionSavedForShutdown;

private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    const int WM_GETMINMAXINFO = 0x0024;
    const int WM_QUERYENDSESSION = 0x0011;
    const int WM_ENDSESSION = 0x0016;

    if (msg == WM_GETMINMAXINFO)
    {
        WmGetMinMaxInfo(hwnd, lParam);
        handled = true;
    }
    else if ((msg == WM_QUERYENDSESSION || (msg == WM_ENDSESSION && wParam != IntPtr.Zero))
             && !_sessionSavedForShutdown)
    {
        _sessionSavedForShutdown = true;
        ViewModel.SaveSession(Left, Top, Width, Height,
            WindowState == WindowState.Maximized);
    }
    return IntPtr.Zero;
}
```

**What doesn't change:** Normal close flow (`OnClosing`) and 10s auto-save timer remain identical.

### Change 3: Fix RelaunchClaudeCodePanes Timing

**File:** `src/Cmux/ViewModels/SurfaceViewModel.cs`

**Current code (broken):**

```csharp
await Task.Delay(1500); // Fixed delay — too short after reboot
foreach (var (paneId, sessionId) in claudePanes)
{
    var cmd = "claude --dangerously-skip-permissions --effort max";
    cmd += string.IsNullOrWhiteSpace(sessionId)
        ? " --resume"
        : $" --resume {sessionId}";
    SendCommandToPane(paneId, cmd);
}
```

**New behavior — use `ShellPromptMarker` event (OSC 133):**

The codebase already has `ShellPromptMarker` event on `TerminalSession` (line 47) which fires when the shell emits an OSC 133 sequence (standard prompt marking from bash/PowerShell). This is far more reliable than polling the buffer for heuristic prompt patterns.

```csharp
foreach (var (paneId, sessionId) in claudePanes)
{
    // Wait for shell prompt using OSC 133 marker (already wired in TerminalSession)
    var promptReady = await WaitForShellPromptMarker(paneId, timeout: TimeSpan.FromSeconds(10));
    if (!promptReady)
        continue; // Shell never became ready — skip this pane

    System.Windows.Application.Current.Dispatcher.Invoke(() =>
    {
        var cmd = "claude --dangerously-skip-permissions --effort max";
        if (!string.IsNullOrWhiteSpace(sessionId))
            cmd += $" --resume {sessionId}";
        SendCommandToPane(paneId, cmd);
    });
    await Task.Delay(200); // Small gap between panes
}
```

**WaitForShellPromptMarker implementation:**

```csharp
private Task<bool> WaitForShellPromptMarker(string paneId, TimeSpan timeout)
{
    if (!_sessions.TryGetValue(paneId, out var session))
        return Task.FromResult(false);

    var tcs = new TaskCompletionSource<bool>();
    void handler(char marker, string? payload)
    {
        if (marker == 'A') // OSC 133;A = prompt start
        {
            session.ShellPromptMarker -= handler;
            tcs.TrySetResult(true);
        }
    }
    session.ShellPromptMarker += handler;

    // Timeout fallback
    _ = Task.Delay(timeout).ContinueWith(_ =>
    {
        session.ShellPromptMarker -= handler;
        tcs.TrySetResult(false);
    });

    return tcs.Task;
}
```

**Key change on --resume:**

- If `sessionId` is present → `--resume <sessionId>` (resume exact conversation)
- If `sessionId` is null → no `--resume` flag (start fresh Claude session — intentional design choice, user-requested)

### Change 4: Shell History Injection

**File:** `src/Cmux/ViewModels/SurfaceViewModel.cs`

**When:** After reboot only (`NeedClaudeResume == true`), before starting sessions.

**What:** For each pane with saved `CommandHistory`, append the commands to the shell's history file:

- **bash:** `~/.bash_history`
- **PowerShell:** `~/AppData/Roaming/Microsoft/Windows/PowerShell/PSReadLine/ConsoleHost_history.txt`

**Implementation:**

```csharp
private static void InjectShellHistory(PaneStateSnapshot snapshot)
{
    if (snapshot.CommandHistory.Count == 0) return;

    // Bash history
    var bashHistory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bash_history");
    File.AppendAllLines(bashHistory, snapshot.CommandHistory);

    // PowerShell history
    var psHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "PowerShell", "PSReadLine");
    var psHistory = Path.Combine(psHistoryDir, "ConsoleHost_history.txt");
    if (Directory.Exists(psHistoryDir))
        File.AppendAllLines(psHistory, snapshot.CommandHistory);
}
```

**Limitations (accepted):**

- History is merged across all panes into global HISTFILE (shell limitation). User confirmed this is acceptable.
- Both bash and PowerShell HISTFILEs are written regardless of which shell the pane used (`PaneStateSnapshot` does not track shell type). This causes benign cross-pollution — bash commands appear in PowerShell history and vice versa. Acceptable trade-off vs. adding a new `ShellType` field.
- `NeedClaudeResume` also triggers on first launch and daemon crash (not only reboot). Since injection is append-only, this causes harmless duplication at worst.

**When called:** During `RestoreSession()`, before pane sessions are started, when `NeedClaudeResume` is true.

### Change 5: Staged Visual Restoration

**File:** `src/Cmux/ViewModels/SurfaceViewModel.cs`

**Flow per pane after reboot:**

```
┌─────────────────────────────────────────────────────────────┐
│ t=0ms    Buffer snapshot displayed (existing code)          │
│          User sees terminal exactly as it was               │
│                                                             │
│ t=0ms    Shell starts in background via daemon              │
│          (ConPTY session created, bash/pwsh launching)      │
│                                                             │
│ t=~2s    OSC 133;A prompt marker received                   │
│          ├─ Claude pane: send claude --resume <sessionId>   │
│          │  Output appends below snapshot naturally          │
│          └─ Shell pane: prompt is ready, cursor blinks      │
│                                                             │
│ Result:  No flash, no blank screen, no visible restart      │
└─────────────────────────────────────────────────────────────┘
```

This is already the natural result of changes 1-4 combined. The snapshot provides visual continuity while processes restart silently. No additional code needed for the visual aspect — it falls out of the architecture.

## Files Modified

| File                                      | Change                                                                                       |
| ----------------------------------------- | -------------------------------------------------------------------------------------------- |
| `src/Cmux/Views/MainWindow.xaml.cs`       | Add `WM_QUERYENDSESSION`/`WM_ENDSESSION` cases to existing `WndProc`                         |
| `src/Cmux/ViewModels/SurfaceViewModel.cs` | `WaitForShellPromptMarker`, history injection, resume logic fix in `RelaunchClaudeCodePanes` |
| `src/Cmux.Core/IPC/DaemonClient.cs`       | Fix `FindDaemonExecutable()` path resolution                                                 |

## Files NOT Modified

- **Daemon** (`DaemonSessionManager.cs`, `TerminalProcess.cs`) — zero changes
- **Models** (`SessionState.cs`, `PaneStateSnapshot.cs`, `Surface.cs`) — no new fields
- **Rendering / UI** — no changes
- **No new files created**

## Risk Assessment

| Risk                                               | Mitigation                                                                                                      |
| -------------------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| `FindDaemonExecutable` fix might be path-dependent | Check daemon-debug.log for `[FindDaemon]` entries, test both dev and deployed scenarios                         |
| OSC 133 not emitted by shell                       | 10s timeout fallback; bash and modern PowerShell both support OSC 133 by default                                |
| Double `SaveSession` during shutdown               | `_sessionSavedForShutdown` flag prevents it                                                                     |
| History injection adds cross-shell commands        | Append-only, benign; users rarely switch shell type mid-session                                                 |
| `WM_ENDSESSION` fires too late                     | `WM_QUERYENDSESSION` fires first as early warning                                                               |
| Regression on happy path (daemon alive)            | All new code is gated behind `NeedClaudeResume == true`                                                         |
| BSOD / power cut bypasses shutdown hook            | Acceptable — 10s auto-save timer provides a recent-enough snapshot. No mitigation needed for hardware failures. |

## Test Plan

1. **Daemon auto-start:** Build and run cmux, verify `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CmuxDaemon` returns the daemon path. Reboot, verify daemon is running before cmux starts (`tasklist | grep cmux-daemon`).

2. **Shutdown hook:** Open cmux with active sessions. Reboot PC. After restart, verify `session.json` timestamp matches the moment of shutdown (not 10s before).

3. **Claude resume after reboot:** Open cmux, start Claude Code in a pane, verify `ClaudeSessionId` is captured in `session.json`. Reboot. Verify Claude relaunches with `--resume <sessionId>`.

4. **Shell history:** Open cmux, run several commands in a bash pane. Reboot. Open cmux, verify arrow-up retrieves previous commands.

5. **Happy path regression:** Open cmux with active sessions. Close cmux (without reboot). Reopen. Verify sessions reconnect seamlessly via daemon (same behavior as today).
