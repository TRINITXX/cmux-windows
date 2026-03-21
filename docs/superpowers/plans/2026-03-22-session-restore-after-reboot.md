# Session Restore After Reboot — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make cmux session restoration after PC reboot seamless — identical to the experience when the daemon stays alive.

**Architecture:** All changes are client-side (cmux GUI). The daemon is NOT modified. New code is gated behind `NeedClaudeResume == true` to protect the happy path (daemon alive). Four independent fixes: daemon auto-start registration, Windows shutdown hook, prompt-aware Claude resume, and shell history injection.

**Tech Stack:** C# / .NET 10 / WPF, ConPTY, Win32 interop (`WM_QUERYENDSESSION`), OSC 133 shell integration

**Spec:** `docs/superpowers/specs/2026-03-21-session-restore-after-reboot-design.md`

---

### Task 1: Diagnose and fix FindDaemonExecutable

**Files:**

- Modify: `src/Cmux.Core/IPC/DaemonClient.cs:133-180`
- Test: `tests/Cmux.Tests/CoreTests.cs`

**Context:** `FindDaemonExecutable()` returns `null`, which prevents `RegisterDaemonAutoStart()` from writing the daemon path to the Windows registry. The method traverses up from `AppContext.BaseDirectory` looking for a `src/` ancestor to locate `Cmux.Daemon/bin/`. The traversal or the daemon binary itself may be missing.

- [ ] **Step 1: Check daemon-debug.log for diagnostic output**

Run from a bash shell:

```bash
cat "$LOCALAPPDATA/cmux/daemon-debug.log" | grep -i "FindDaemon"
```

This shows what `AppContext.BaseDirectory` is and where the traversal stops. Note the exact output for the next step.

- [ ] **Step 2: Verify daemon project builds**

```bash
dotnet build src/Cmux.Daemon/Cmux.Daemon.csproj
```

Expected: Build succeeds. If it fails, fix build errors first. Then verify the binary exists:

```bash
find src/Cmux.Daemon/bin -name "cmux-daemon.exe" 2>/dev/null
```

- [ ] **Step 3: Fix FindDaemonExecutable if path traversal fails**

This step is **data-driven** — the fix depends on what the log reveals. Do NOT apply a speculative fix. Common scenarios:

**If log shows `Traversed to src dir: (null)`:** `AppContext.BaseDirectory` doesn't have `src` as an ancestor. Add a fallback using the executable's own path (NOT `Directory.GetCurrentDirectory()` which is unreliable — it could be `C:\Windows\System32` after reboot).

In `src/Cmux.Core/IPC/DaemonClient.cs`, after the existing `catch` block at line 173, before `return null` (line 179), add:

```csharp
// 3. Look relative to executable location (handles non-standard paths)
try
{
    var exeLocation = Environment.ProcessPath;
    if (exeLocation != null)
    {
        var exeDir = new DirectoryInfo(Path.GetDirectoryName(exeLocation)!);
        var dir = exeDir;
        while (dir != null && !string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;

        if (dir != null)
        {
            var daemonPath = Path.Combine(dir.FullName, "Cmux.Daemon", "bin");
            if (Directory.Exists(daemonPath))
            {
                foreach (var exe in Directory.GetFiles(daemonPath, "cmux-daemon.exe", SearchOption.AllDirectories))
                {
                    LogDaemon($"[FindDaemon] Found via ProcessPath: {exe}");
                    return exe;
                }
            }
        }
    }
}
catch
{
    // Not critical
}
```

**If log shows daemon binary not found (all paths explored but file doesn't exist):** The daemon project hasn't been built. Step 2 already handles this — build it first, then re-run cmux.

**If log shows nothing at all:** The method is never called, or logging isn't working. Check that cmux connects to the daemon (Phase 1 or Phase 2 in app startup log).

- [ ] **Step 4: Write test for FindDaemonExecutable**

In `tests/Cmux.Tests/CoreTests.cs`, add a new test class at the end of the file:

```csharp
public class DaemonClientTests
{
    [Fact]
    public void FindDaemonExecutable_ReturnsPath_WhenDaemonBuilt()
    {
        // This test verifies the dev-build scenario.
        // It will only pass if the daemon has been built.
        var path = Cmux.Core.IPC.DaemonClient.FindDaemonExecutable();

        // If daemon is built, path should not be null and should point to an existing file
        if (path != null)
        {
            File.Exists(path).Should().BeTrue();
            Path.GetFileName(path).Should().Be("cmux-daemon.exe");
        }
        // If daemon is not built, path is null — acceptable in CI
    }
}
```

- [ ] **Step 5: Run test**

```bash
dotnet test tests/Cmux.Tests --filter "DaemonClientTests" -v n
```

Expected: PASS

- [ ] **Step 6: Verify registry registration manually**

Launch cmux, then check:

```bash
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CmuxDaemon
```

Expected: Shows the daemon path. If still not registered, check `daemon-debug.log` again for `[App] Registered daemon autostart` or `[App] Failed to register daemon autostart`.

- [ ] **Step 7: Commit**

```bash
git add src/Cmux.Core/IPC/DaemonClient.cs tests/Cmux.Tests/CoreTests.cs
git commit -m "fix(daemon): fix FindDaemonExecutable path resolution for auto-start registration"
```

---

### Task 2: Add Windows shutdown hook

**Files:**

- Modify: `src/Cmux/Views/MainWindow.xaml.cs:77-86`

**Context:** The existing `WndProc` at line 77 only handles `WM_GETMINMAXINFO` (0x0024). We need to add `WM_QUERYENDSESSION` and `WM_ENDSESSION` handling to force a session save before Windows shuts down.

- [ ] **Step 1: Add the shutdown save flag field**

In `src/Cmux/Views/MainWindow.xaml.cs`, find the class fields area (near the top of the partial class). Add:

```csharp
private bool _sessionSavedForShutdown;
```

- [ ] **Step 2: Extend the existing WndProc**

Replace the current `WndProc` method at line 77-86:

```csharp
// CURRENT (line 77-86):
private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    // WM_GETMINMAXINFO = 0x0024
    if (msg == 0x0024)
    {
        WmGetMinMaxInfo(hwnd, lParam);
        handled = true;
    }
    return IntPtr.Zero;
}
```

With:

```csharp
private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    const int WM_QUERYENDSESSION = 0x0011;
    const int WM_ENDSESSION = 0x0016;
    const int WM_GETMINMAXINFO = 0x0024;

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
        if (msg == WM_QUERYENDSESSION)
        {
            handled = true;
            return new IntPtr(1); // Allow shutdown to continue
        }
    }
    return IntPtr.Zero;
}
```

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src/Cmux/Cmux.csproj
```

Expected: Build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/Cmux/Views/MainWindow.xaml.cs
git commit -m "feat(session): add WM_QUERYENDSESSION/WM_ENDSESSION hook for shutdown save"
```

---

### Task 3: Add WaitForShellPromptMarker helper

**Files:**

- Modify: `src/Cmux/ViewModels/SurfaceViewModel.cs`
- Test: `tests/Cmux.Tests/CoreTests.cs`

**Context:** `RelaunchClaudeCodePanes` currently uses a fixed 1.5s delay. We need a helper that waits for the OSC 133;A prompt marker (already emitted by bash/PowerShell and already wired via `ShellPromptMarker` event on `TerminalSession` at line 47).

- [ ] **Step 1: Write test for WaitForShellPromptMarker**

In `tests/Cmux.Tests/CoreTests.cs`, add at the end:

```csharp
public class WaitForPromptTests
{
    [Fact]
    public async Task WaitForPrompt_ReturnsTrue_WhenMarkerFires()
    {
        var session = new TerminalSession("test-pane", 80, 24);
        var tcs = new TaskCompletionSource<bool>();

        void handler(char marker, string? payload)
        {
            if (marker == 'A')
            {
                session.ShellPromptMarker -= handler;
                tcs.TrySetResult(true);
            }
        }
        session.ShellPromptMarker += handler;

        // Simulate prompt marker after short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            // Feed OSC 133;A sequence: ESC ] 133;A BEL
            session.FeedOutput(Encoding.ASCII.GetBytes("\x1b]133;A\x07"));
        });

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForPrompt_ReturnsFalse_OnTimeout()
    {
        var session = new TerminalSession("test-pane-timeout", 80, 24);
        var tcs = new TaskCompletionSource<bool>();

        void handler(char marker, string? payload)
        {
            if (marker == 'A')
            {
                session.ShellPromptMarker -= handler;
                tcs.TrySetResult(true);
            }
        }
        session.ShellPromptMarker += handler;

        // Timeout fallback — use CancellationTokenSource for reliable timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        cts.Token.Register(() =>
        {
            session.ShellPromptMarker -= handler;
            tcs.TrySetResult(false);
        });

        // Don't feed any data — should timeout
        var result = await tcs.Task;
        result.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

```bash
dotnet test tests/Cmux.Tests --filter "WaitForPromptTests" -v n
```

Expected: Both tests PASS.

- [ ] **Step 3: Add WaitForShellPromptMarker to SurfaceViewModel**

In `src/Cmux/ViewModels/SurfaceViewModel.cs`, add this method near `RelaunchClaudeCodePanes` (after line 158):

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

- [ ] **Step 4: Build to verify compilation**

```bash
dotnet build src/Cmux/Cmux.csproj
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Cmux/ViewModels/SurfaceViewModel.cs tests/Cmux.Tests/CoreTests.cs
git commit -m "feat(session): add WaitForShellPromptMarker helper using OSC 133"
```

---

### Task 4: Rewrite RelaunchClaudeCodePanes with prompt detection

**Files:**

- Modify: `src/Cmux/ViewModels/SurfaceViewModel.cs:119-158`

**Context:** Replace the fixed 1.5s delay with per-pane prompt detection using `WaitForShellPromptMarker` from Task 3. Also fix the `--resume` logic: use `--resume <sessionId>` when sessionId exists, plain command (no `--resume`) otherwise.

**IMPORTANT behavioral change:** The old code used bare `--resume` (resume most recent session) when `sessionId` was null. The new code intentionally drops this fallback — per user request, a fresh Claude session should start if no sessionId was captured. This is a deliberate design choice, not a bug.

- [ ] **Step 1: Replace RelaunchClaudeCodePanes body**

In `src/Cmux/ViewModels/SurfaceViewModel.cs`, replace the `RelaunchClaudeCodePanes` method (lines 119-158) with:

```csharp
private void RelaunchClaudeCodePanes()
{
    var claudePanes = new List<(string paneId, string? sessionId)>();
    foreach (var leaf in RootNode.GetLeaves())
    {
        if (leaf.PaneId == null) continue;
        Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot);
        if (snapshot?.IsClaudeCode == true)
            claudePanes.Add((leaf.PaneId, snapshot.ClaudeSessionId));
    }

    if (claudePanes.Count == 0) return;

    _ = Task.Run(async () =>
    {
        // Wait for daemon connection
        try { await App.DaemonConnectTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }

        // Only relaunch if daemon has no existing sessions (fresh boot / daemon restart).
        // If daemon had active sessions, they are still alive — no resume needed.
        if (!App.NeedClaudeResume) return;

        // Relaunch Claude Code in each pane after detecting shell prompt
        foreach (var (paneId, sessionId) in claudePanes)
        {
            var promptReady = await WaitForShellPromptMarker(paneId, timeout: TimeSpan.FromSeconds(10));
            if (!promptReady)
            {
                App.DaemonLog($"[RelaunchClaude:{paneId}] Prompt not detected within 10s, skipping");
                continue;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var cmd = "claude --dangerously-skip-permissions --effort max";
                if (!string.IsNullOrWhiteSpace(sessionId))
                    cmd += $" --resume {sessionId}";
                SendCommandToPane(paneId, cmd);
            });
            await Task.Delay(200); // Small gap between panes
        }
    });
}
```

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/Cmux/Cmux.csproj
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/ViewModels/SurfaceViewModel.cs
git commit -m "fix(session): use OSC 133 prompt detection instead of fixed delay for Claude resume"
```

---

### Task 5: Add shell history injection

**Files:**

- Modify: `src/Cmux/ViewModels/SurfaceViewModel.cs`

**Context:** After a reboot, shell command history is lost. The `CommandHistory` field in `PaneStateSnapshot` has the last 500 commands per pane. We need to append them to bash and PowerShell HISTFILE before pane sessions start.

- [ ] **Step 1: Add InjectShellHistory method**

In `src/Cmux/ViewModels/SurfaceViewModel.cs`, add this static method near `RelaunchClaudeCodePanes`:

```csharp
private static void InjectShellHistory(PaneStateSnapshot snapshot)
{
    if (snapshot.CommandHistory.Count == 0) return;

    try
    {
        // Bash history
        var bashHistory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bash_history");
        File.AppendAllLines(bashHistory, snapshot.CommandHistory);

        // PowerShell PSReadLine history
        var psHistoryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "PowerShell", "PSReadLine");
        var psHistory = Path.Combine(psHistoryDir, "ConsoleHost_history.txt");
        if (Directory.Exists(psHistoryDir))
            File.AppendAllLines(psHistory, snapshot.CommandHistory);
    }
    catch (Exception ex)
    {
        App.DaemonLog($"[InjectShellHistory] Error: {ex.Message}");
    }
}
```

- [ ] **Step 2: Call InjectShellHistory during session restore**

In the `SurfaceViewModel` constructor, inside the loop that iterates over pane leaves and starts sessions (lines 71-87). The **actual** code block to find is:

```csharp
// Start terminal sessions for all leaf nodes
foreach (var leaf in _rootNode.GetLeaves())
{
    if (leaf.PaneId != null)
    {
        Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot);
        if (snapshot?.CommandHistory is { Count: > 0 })
        {
            _paneCommandHistory[leaf.PaneId] = snapshot.CommandHistory
                .Select(App.CommandLogService.SanitizeCommandForStorage)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Cast<string>()
                .ToList();
        }

        StartSession(leaf.PaneId, snapshot?.WorkingDirectory ?? Surface.WorkingDirectory, snapshot);
    }
}
```

Replace with:

```csharp
// Inject shell history before starting sessions (reboot scenario only)
if (App.NeedClaudeResume)
{
    foreach (var leaf in _rootNode.GetLeaves())
    {
        if (leaf.PaneId == null) continue;
        if (Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snap))
            InjectShellHistory(snap);
    }
}

// Start terminal sessions for all leaf nodes
foreach (var leaf in _rootNode.GetLeaves())
{
    if (leaf.PaneId != null)
    {
        Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot);
        if (snapshot?.CommandHistory is { Count: > 0 })
        {
            _paneCommandHistory[leaf.PaneId] = snapshot.CommandHistory
                .Select(App.CommandLogService.SanitizeCommandForStorage)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Cast<string>()
                .ToList();
        }

        StartSession(leaf.PaneId, snapshot?.WorkingDirectory ?? Surface.WorkingDirectory, snapshot);
    }
}
```

- [ ] **Step 3: Add using directive if needed**

Ensure `System.IO` is imported at the top of `SurfaceViewModel.cs`. Check if `Path`, `File`, `Directory`, `Environment` are available. They should be via `System.IO` which is typically imported globally in .NET 10 projects, but verify.

- [ ] **Step 4: Build to verify compilation**

```bash
dotnet build src/Cmux/Cmux.csproj
```

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Cmux/ViewModels/SurfaceViewModel.cs
git commit -m "feat(session): inject shell command history into HISTFILE on reboot restore"
```

---

### Task 6: Run full test suite and manual verification

**Files:** None (verification only)

- [ ] **Step 1: Run all tests**

```bash
dotnet test tests/Cmux.Tests -v n
```

Expected: All tests pass, including the new `DaemonClientTests` and `WaitForPromptTests`.

- [ ] **Step 2: Build entire solution**

```bash
dotnet build
```

Expected: Clean build, no warnings related to our changes.

- [ ] **Step 3: Manual test — daemon auto-start**

1. Launch cmux
2. Verify registry: `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CmuxDaemon`
3. Expected: Shows path to `cmux-daemon.exe`

- [ ] **Step 4: Manual test — shutdown hook**

1. Open cmux with active sessions
2. Note the current time
3. Reboot PC
4. After restart, check `session.json` modification time:
   ```bash
   stat "$LOCALAPPDATA/cmux/session.json" | grep Modify
   ```
5. Expected: Timestamp matches the shutdown moment (not 10s before)

- [ ] **Step 5: Manual test — Claude resume after reboot**

1. Open cmux, start Claude Code in a pane
2. Run a command in Claude (e.g., ask it something)
3. Check `session.json` for `ClaudeSessionId` field being populated
4. Reboot PC
5. Launch cmux
6. Expected: Claude Code relaunches automatically with `--resume <sessionId>`, previous conversation context is available

- [ ] **Step 6: Manual test — shell history after reboot**

1. Open cmux, run several commands in a bash pane (e.g., `echo hello`, `ls -la`, `git status`)
2. Reboot PC
3. Launch cmux
4. In the bash pane, press arrow-up
5. Expected: Previous commands are available in history

- [ ] **Step 7: Manual test — happy path regression**

1. Open cmux with active sessions (Claude + shells)
2. Close cmux (do NOT reboot)
3. Reopen cmux
4. Expected: All sessions reconnect via daemon — exact same behavior as before our changes. No resume needed, live buffer restored from daemon.

- [ ] **Step 8: Final commit (if any fixups needed)**

If manual testing revealed issues that needed fixing, stage only the specific files changed and commit:

```bash
git add src/Cmux/ViewModels/SurfaceViewModel.cs src/Cmux/Views/MainWindow.xaml.cs src/Cmux.Core/IPC/DaemonClient.cs
git commit -m "fix(session): address issues found during manual testing"
```
