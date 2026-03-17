# cmux-windows Custom Adaptations — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Customize cmux-windows with workspace templates, Claude Code status detection, toolbar buttons (SSH/PowerShell), auto-Claude layouts, browser auto-open, flash focus, right-click Explorer, and folder picker for new workspaces.

**Architecture:** Isolated extension layer — new services in `Cmux.Core/Services/`, new models in `Cmux.Core/Models/`, minimal edits to existing UI files. Services integrate via existing events (`RawOutputReceived`, `BellReceived`, `NotificationReceived`, `ProcessExited`).

**Tech Stack:** C# / .NET 10 / WPF / ConPTY / CommunityToolkit.Mvvm

**Spec:** `docs/superpowers/specs/2026-03-17-cmux-custom-adaptations-design.md`

---

## File Map

### New Files

| File                                                 | Responsibility                       |
| ---------------------------------------------------- | ------------------------------------ |
| `src/Cmux.Core/Models/WorkspaceTemplate.cs`          | Template data model                  |
| `src/Cmux.Core/Models/ClaudeStatus.cs`               | Status enum                          |
| `src/Cmux.Core/Services/WorkspaceTemplateService.cs` | Template CRUD + defaults             |
| `src/Cmux.Core/Services/ClaudeCodeStatusService.cs`  | Agent status detection state machine |
| `src/Cmux.Core/Services/PortDetectionService.cs`     | Dev server URL detection             |

### Modified Files

| File                                      | What Changes                                                                              |
| ----------------------------------------- | ----------------------------------------------------------------------------------------- |
| `src/Cmux.Core/Config/CmuxSettings.cs`    | Add settings: `SshHost`, `SshKeyPath`, `AutoOpenBrowserOnDevServer`, `WorkspaceTemplates` |
| `src/Cmux/App.xaml.cs`                    | Initialize new services                                                                   |
| `src/Cmux/Views/MainWindow.xaml`          | Add toolbar buttons (SSH, PowerShell), template dropdown in sidebar                       |
| `src/Cmux/Views/MainWindow.xaml.cs`       | Add button handlers, modify layout handlers to launch Claude Code                         |
| `src/Cmux/ViewModels/MainViewModel.cs`    | Add `CreateWorkspaceFromTemplate()`, `CreateWorkspaceWithFolderPicker()`                  |
| `src/Cmux/ViewModels/SurfaceViewModel.cs` | Expose `ClaudeStatus` per pane, method to send command to pane                            |
| `src/Cmux/Controls/SurfaceTabBar.xaml`    | Add status dot element                                                                    |
| `src/Cmux/Controls/SurfaceTabBar.xaml.cs` | Bind status dot to `ClaudeCodeStatusService`                                              |
| `src/Cmux/Controls/SplitPaneContainer.cs` | Add flash border animation on focus change                                                |
| `src/Cmux/Controls/TerminalControl.cs`    | Add status bar overlay, "Open in Explorer" context menu item                              |

---

## Task 1: Settings + Models (foundation)

**Files:**

- Modify: `src/Cmux.Core/Config/CmuxSettings.cs:50-55`
- Create: `src/Cmux.Core/Models/WorkspaceTemplate.cs`
- Create: `src/Cmux.Core/Models/ClaudeStatus.cs`

- [ ] **Step 1: Add new settings to CmuxSettings**

In `src/Cmux.Core/Config/CmuxSettings.cs`, add after line 54 (`public AgentSettings Agent`):

```csharp
// ── Custom ────────────────────────────────────────────────
public string SshHost { get; set; } = "trinitx@192.168.1.32";
public string SshKeyPath { get; set; } = "~/.ssh/id_ed25519";
public bool AutoOpenBrowserOnDevServer { get; set; } = true;
public List<WorkspaceTemplate> WorkspaceTemplates { get; set; } = [];
```

Add `using Cmux.Core.Models;` at top.

- [ ] **Step 2: Create WorkspaceTemplate model**

Create `src/Cmux.Core/Models/WorkspaceTemplate.cs`:

```csharp
namespace Cmux.Core.Models;

public class WorkspaceTemplate
{
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? StartupCommand { get; set; }
    public string? IconGlyph { get; set; }
    public string? AccentColor { get; set; }
}
```

- [ ] **Step 3: Create ClaudeStatus enum**

Create `src/Cmux.Core/Models/ClaudeStatus.cs`:

```csharp
namespace Cmux.Core.Models;

public enum ClaudeStatus
{
    Idle,
    Working,
    WaitingForInput
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/Cmux.Core/Config/CmuxSettings.cs src/Cmux.Core/Models/
git commit -m "feat: add settings and models for custom adaptations"
```

---

## Task 2: WorkspaceTemplateService

**Files:**

- Create: `src/Cmux.Core/Services/WorkspaceTemplateService.cs`

- [ ] **Step 1: Create WorkspaceTemplateService**

Create `src/Cmux.Core/Services/WorkspaceTemplateService.cs`:

```csharp
using Cmux.Core.Config;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

public class WorkspaceTemplateService
{
    private static readonly List<WorkspaceTemplate> Defaults =
    [
        new() { Name = "VTC-Planner", Directory = "C:/Users/TRINITX/Desktop/VTC-Planner", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8B7" },
        new() { Name = "FidelyPass", Directory = "C:/Users/TRINITX/Desktop/FidelyPass", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8D7" },
        new() { Name = "Qwitt", Directory = "C:/Users/TRINITX/Desktop/Qwitt", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE8F1" },
        new() { Name = "dress_up", Directory = "C:/Users/TRINITX/Desktop/dress_up", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE771" },
        new() { Name = "Email verifier", Directory = "C:/Users/TRINITX/Desktop/Email verifier", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE715" },
        new() { Name = "Scraping", Directory = "C:/Users/TRINITX/Desktop/Scraping", StartupCommand = "claude --dangerously-skip-permissions --effort max", IconGlyph = "\uE774" },
    ];

    public List<WorkspaceTemplate> GetTemplates()
    {
        var settings = SettingsService.Current;
        if (settings.WorkspaceTemplates.Count == 0)
        {
            settings.WorkspaceTemplates = new List<WorkspaceTemplate>(Defaults);
            SettingsService.Save();
        }
        return settings.WorkspaceTemplates;
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux.Core/Services/WorkspaceTemplateService.cs
git commit -m "feat: add workspace template service with default project templates"
```

---

## Task 3: ClaudeCodeStatusService

**Files:**

- Create: `src/Cmux.Core/Services/ClaudeCodeStatusService.cs`

- [ ] **Step 1: Create ClaudeCodeStatusService**

Create `src/Cmux.Core/Services/ClaudeCodeStatusService.cs`:

```csharp
using System.Collections.Concurrent;
using Cmux.Core.Models;
using Cmux.Core.Terminal;

namespace Cmux.Core.Services;

public class ClaudeCodeStatusService : IDisposable
{
    private readonly ConcurrentDictionary<string, PaneState> _paneStates = new();
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    public event Action<string, ClaudeStatus, ClaudeStatus>? StatusChanged;

    public ClaudeCodeStatusService()
    {
        _pollTimer = new System.Timers.Timer(2000);
        _pollTimer.Elapsed += (_, _) => PollProcesses();
        _pollTimer.Start();
    }

    public void RegisterPane(string paneId, TerminalSession session)
    {
        var state = new PaneState { Session = session };
        _paneStates[paneId] = state;

        session.RawOutputReceived += _ =>
        {
            state.LastOutputTime = DateTime.UtcNow;
            if (state.Status == ClaudeStatus.WaitingForInput)
                TransitionTo(paneId, state, ClaudeStatus.Working);
        };

        session.BellReceived += () => state.NotificationTime = DateTime.UtcNow;
        session.NotificationReceived += (_, _, _) => state.NotificationTime = DateTime.UtcNow;
    }

    public void UnregisterPane(string paneId)
    {
        _paneStates.TryRemove(paneId, out _);
    }

    public ClaudeStatus GetStatus(string paneId)
    {
        return _paneStates.TryGetValue(paneId, out var state) ? state.Status : ClaudeStatus.Idle;
    }

    private void PollProcesses()
    {
        if (_disposed) return;

        var pids = new Dictionary<string, int>();
        foreach (var (paneId, state) in _paneStates)
        {
            var pid = state.Session.ProcessId;
            if (pid.HasValue) pids[paneId] = pid.Value;
        }

        if (pids.Count == 0) return;

        // Batched WMI query for all shell PIDs
        var parentPidList = string.Join(",", pids.Values.Distinct());
        var childNames = new Dictionary<int, List<string>>();

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name, ParentProcessId FROM Win32_Process WHERE ParentProcessId IN ({parentPidList})");
            foreach (var obj in searcher.Get())
            {
                var parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                var name = obj["Name"]?.ToString() ?? "";
                if (!childNames.ContainsKey(parentPid))
                    childNames[parentPid] = [];
                childNames[parentPid].Add(name);
            }
        }
        catch { return; }

        var now = DateTime.UtcNow;
        foreach (var (paneId, shellPid) in pids)
        {
            if (!_paneStates.TryGetValue(paneId, out var state)) continue;

            var children = childNames.GetValueOrDefault(shellPid, []);
            var hasClaude = children.Any(n => n.Contains("claude", StringComparison.OrdinalIgnoreCase));

            if (!hasClaude)
            {
                TransitionTo(paneId, state, ClaudeStatus.Idle);
                continue;
            }

            // Claude is running
            var outputAge = (now - state.LastOutputTime).TotalSeconds;
            var notifAge = state.NotificationTime.HasValue
                ? (now - state.NotificationTime.Value).TotalSeconds
                : double.MaxValue;

            if (notifAge < 30 && outputAge > 2)
            {
                TransitionTo(paneId, state, ClaudeStatus.WaitingForInput);
            }
            else if (outputAge < 3)
            {
                TransitionTo(paneId, state, ClaudeStatus.Working);
            }
            // else keep current state (avoid flicker)
        }
    }

    private void TransitionTo(string paneId, PaneState state, ClaudeStatus newStatus)
    {
        var old = state.Status;
        if (old == newStatus) return;
        state.Status = newStatus;
        if (newStatus != ClaudeStatus.WaitingForInput)
            state.NotificationTime = null;
        StatusChanged?.Invoke(paneId, old, newStatus);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }

    private class PaneState
    {
        public TerminalSession Session { get; init; } = null!;
        public ClaudeStatus Status { get; set; } = ClaudeStatus.Idle;
        public DateTime LastOutputTime { get; set; } = DateTime.MinValue;
        public DateTime? NotificationTime { get; set; }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux.Core/Services/ClaudeCodeStatusService.cs
git commit -m "feat: add Claude Code status detection service with batched WMI polling"
```

---

## Task 4: PortDetectionService

**Files:**

- Create: `src/Cmux.Core/Services/PortDetectionService.cs`

- [ ] **Step 1: Create PortDetectionService**

Create `src/Cmux.Core/Services/PortDetectionService.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cmux.Core.Config;
using Cmux.Core.Terminal;

namespace Cmux.Core.Services;

public class PortDetectionService
{
    private static readonly Regex UrlPattern = new(
        @"https?://(?:localhost|127\.0\.0\.1):\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, HashSet<string>> _detectedUrls = new();

    public event Action<string, string>? DevServerStarted; // paneId, url

    public void RegisterPane(string paneId, TerminalSession session)
    {
        _detectedUrls[paneId] = [];

        session.RawOutputReceived += data =>
        {
            if (!SettingsService.Current.AutoOpenBrowserOnDevServer) return;

            var text = Encoding.UTF8.GetString(data);
            var matches = UrlPattern.Matches(text);
            foreach (Match match in matches)
            {
                var url = match.Value;
                var urls = _detectedUrls.GetOrAdd(paneId, _ => []);
                if (urls.Add(url))
                    DevServerStarted?.Invoke(paneId, url);
            }
        };
    }

    public void UnregisterPane(string paneId)
    {
        _detectedUrls.TryRemove(paneId, out _);
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux.Core/Services/PortDetectionService.cs
git commit -m "feat: add port detection service for auto-opening dev server URLs"
```

---

## Task 5: Initialize services in App.xaml.cs

**Files:**

- Modify: `src/Cmux/App.xaml.cs:14-21`

- [ ] **Step 1: Add service properties and initialization**

In `src/Cmux/App.xaml.cs`, add after line 19 (`public static AgentRuntimeService AgentRuntime`):

```csharp
public static ClaudeCodeStatusService ClaudeStatusService { get; } = new();
public static PortDetectionService PortDetectionService { get; } = new();
public static WorkspaceTemplateService TemplateService { get; } = new();
```

Add `using Cmux.Core.Services;` if not already present (it is at line 5).

In `OnExit`, add before `base.OnExit(e)`:

```csharp
ClaudeStatusService.Dispose();
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/App.xaml.cs
git commit -m "feat: initialize custom services on app startup"
```

---

## Task 6: Wire services into SurfaceViewModel

**Files:**

- Modify: `src/Cmux/ViewModels/SurfaceViewModel.cs:478-477` (WireSessionEvents method)

- [ ] **Step 1: Register panes with status and port services**

In `SurfaceViewModel.cs`, find the `WireSessionEvents` method (line ~479). At the end of it, add:

```csharp
App.ClaudeStatusService.RegisterPane(paneId, session);
App.PortDetectionService.RegisterPane(paneId, session);
```

In the `ClosePane` method (line ~553), after `session.Dispose()` and before `_sessions.Remove(paneId)`, add:

```csharp
App.ClaudeStatusService.UnregisterPane(paneId);
App.PortDetectionService.UnregisterPane(paneId);
```

In the `Dispose` method (line ~644), before `_sessions.Clear()`, add:

```csharp
foreach (var paneId in _sessions.Keys)
{
    App.ClaudeStatusService.UnregisterPane(paneId);
    App.PortDetectionService.UnregisterPane(paneId);
}
```

- [ ] **Step 2: Add helper to send command to a pane**

Add this method to `SurfaceViewModel`:

```csharp
public void SendCommandToPane(string paneId, string command)
{
    if (_sessions.TryGetValue(paneId, out var session))
        session.Write(command + "\r");
}

public void SendCommandToAllPanes(string command)
{
    foreach (var (paneId, session) in _sessions)
        session.Write(command + "\r");
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Cmux/ViewModels/SurfaceViewModel.cs
git commit -m "feat: wire status and port services into surface lifecycle"
```

---

## Task 7: Workspace templates UI + folder picker

**Files:**

- Modify: `src/Cmux/ViewModels/MainViewModel.cs:77-87`
- Modify: `src/Cmux/Views/MainWindow.xaml` (sidebar section)
- Modify: `src/Cmux/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Add CreateWorkspaceFromTemplate to MainViewModel**

In `src/Cmux/ViewModels/MainViewModel.cs`, add after the `CreateNewWorkspace()` method:

```csharp
public void CreateWorkspaceFromTemplate(WorkspaceTemplate template)
{
    var workspace = new Workspace
    {
        Name = template.Name,
        IconGlyph = template.IconGlyph,
        AccentColor = template.AccentColor,
        WorkingDirectory = template.Directory,
    };
    var surface = new Surface { Name = "Terminal 1" };
    workspace.Surfaces.Add(surface);
    workspace.SelectedSurface = surface;

    var vm = new WorkspaceViewModel(workspace, _notificationService);
    Workspaces.Add(vm);
    SelectedWorkspace = vm;

    // Send cd + startup command after session initializes
    _ = Task.Run(async () =>
    {
        await Task.Delay(500); // Wait for session to start
        Application.Current.Dispatcher.Invoke(() =>
        {
            var surfaceVm = vm.SelectedSurface;
            if (surfaceVm?.FocusedPaneId != null)
            {
                surfaceVm.SendCommandToPane(surfaceVm.FocusedPaneId, $"cd \"{template.Directory}\"");
                if (!string.IsNullOrWhiteSpace(template.StartupCommand))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        Application.Current.Dispatcher.Invoke(() =>
                            surfaceVm.SendCommandToPane(surfaceVm.FocusedPaneId!, template.StartupCommand));
                    });
                }
            }
        });
    });
}

public void CreateWorkspaceWithFolderPicker()
{
    var dialog = new Microsoft.Win32.OpenFolderDialog
    {
        Title = "Choose workspace directory"
    };

    if (dialog.ShowDialog() == true)
    {
        var folderName = System.IO.Path.GetFileName(dialog.FolderName) ?? "Workspace";
        var workspace = new Workspace
        {
            Name = folderName,
            WorkingDirectory = dialog.FolderName,
        };
        var surface = new Surface { Name = "Terminal 1" };
        workspace.Surfaces.Add(surface);
        workspace.SelectedSurface = surface;

        var vm = new WorkspaceViewModel(workspace, _notificationService);
        Workspaces.Add(vm);
        SelectedWorkspace = vm;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Application.Current.Dispatcher.Invoke(() =>
            {
                var surfaceVm = vm.SelectedSurface;
                if (surfaceVm?.FocusedPaneId != null)
                    surfaceVm.SendCommandToPane(surfaceVm.FocusedPaneId, $"cd \"{dialog.FolderName}\"");
            });
        });
    }
}
```

Add `using Cmux.Core.Models;` at top.

- [ ] **Step 2: Add template dropdown button in MainWindow.xaml sidebar**

Find the "New Workspace" button in the sidebar. Add a dropdown button next to it. Look for the existing button with `CreateNewWorkspaceCommand` and add the template button nearby.

In `MainWindow.xaml.cs`, add handlers:

```csharp
private void TemplateButton_Click(object sender, RoutedEventArgs e)
{
    var button = sender as Button;
    if (button?.ContextMenu != null)
    {
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
}

private void TemplateMenuItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is MenuItem item && item.Tag is WorkspaceTemplate template)
        ViewModel.CreateWorkspaceFromTemplate(template);
}

private void NewWorkspaceWithFolder_Click(object sender, RoutedEventArgs e)
{
    ViewModel.CreateWorkspaceWithFolderPicker();
}
```

In `MainWindow.xaml`, find the sidebar new-workspace button area and add:

```xml
<Button Style="{StaticResource IconButton}" ToolTip="New from Template" Click="TemplateButton_Click">
    <TextBlock Text="&#xE8F1;" FontFamily="Segoe MDL2 Assets" FontSize="14" />
    <Button.ContextMenu>
        <ContextMenu x:Name="TemplateMenu" />
    </Button.ContextMenu>
</Button>
<Button Style="{StaticResource IconButton}" ToolTip="New Workspace (Choose Folder)" Click="NewWorkspaceWithFolder_Click">
    <TextBlock Text="&#xED25;" FontFamily="Segoe MDL2 Assets" FontSize="14" />
</Button>
```

Populate the template context menu in `MainWindow.xaml.cs` `OnLoaded` or constructor:

```csharp
private void PopulateTemplateMenu()
{
    TemplateMenu.Items.Clear();
    foreach (var template in App.TemplateService.GetTemplates())
    {
        var item = new MenuItem
        {
            Header = template.Name,
            Tag = template,
        };
        item.Click += TemplateMenuItem_Click;
        TemplateMenu.Items.Add(item);
    }
}
```

Call `PopulateTemplateMenu()` from `OnLoaded`.

- [ ] **Step 3: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 4: Test manually**

Launch app, click template button, select a project → workspace should open with cd + claude command.

- [ ] **Step 5: Commit**

```bash
git add src/Cmux/ViewModels/MainViewModel.cs src/Cmux/Views/MainWindow.xaml src/Cmux/Views/MainWindow.xaml.cs
git commit -m "feat: add workspace templates dropdown and folder picker for new workspaces"
```

---

## Task 8: Toolbar buttons (SSH Mac + PowerShell) + Layout Claude Code

**Files:**

- Modify: `src/Cmux/Views/MainWindow.xaml:425-435`
- Modify: `src/Cmux/Views/MainWindow.xaml.cs:650-652`

- [ ] **Step 1: Add SSH and PowerShell buttons in XAML**

In `src/Cmux/Views/MainWindow.xaml`, after the Agent Chat button (line ~439), before `</StackPanel>`, add:

```xml
<Rectangle Width="1" Height="16" Fill="{StaticResource BorderBrush}" Margin="4,0" />
<Button Style="{StaticResource IconButton}" ToolTip="New PowerShell Pane"
        Click="ToolbarPowerShell_Click">
    <TextBlock Text="&#xE756;" FontFamily="Segoe MDL2 Assets" FontSize="12" />
</Button>
<Button Style="{StaticResource IconButton}" ToolTip="SSH to macOS VM"
        Click="ToolbarSshMac_Click">
    <TextBlock Text="&#xE838;" FontFamily="Segoe MDL2 Assets" FontSize="12" />
</Button>
```

Note: `\uE838` is a Segoe MDL2 device icon as placeholder. For a real Apple logo, a custom font or image would be needed — use this for now and refine later.

- [ ] **Step 2: Add button handlers in MainWindow.xaml.cs**

Add to `MainWindow.xaml.cs`:

```csharp
private void ToolbarPowerShell_Click(object sender, RoutedEventArgs e)
{
    var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
    if (surface == null) return;
    surface.SplitRight();
    // New pane gets cwd from the focused pane automatically via SplitFocused logic
}

private void ToolbarSshMac_Click(object sender, RoutedEventArgs e)
{
    var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
    if (surface == null) return;
    surface.SplitRight();

    _ = Task.Run(async () =>
    {
        await Task.Delay(500);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var settings = Cmux.Core.Config.SettingsService.Current;
            var sshCmd = $"ssh {settings.SshHost}";
            if (surface.FocusedPaneId != null)
                surface.SendCommandToPane(surface.FocusedPaneId, sshCmd);
        });
    });
}
```

- [ ] **Step 3: Modify layout handlers to launch Claude Code**

In `MainWindow.xaml.cs`, replace the layout handler one-liners (lines 650-652):

```csharp
private void ToolbarLayout2Col_Click(object sender, RoutedEventArgs e)
{
    ApplyLayout(2, 1);
    LaunchClaudeInAllPanes();
}

private void ToolbarLayoutGrid_Click(object sender, RoutedEventArgs e)
{
    ApplyLayout(2, 2);
    LaunchClaudeInAllPanes();
}

private void ToolbarLayoutMainStack_Click(object sender, RoutedEventArgs e)
{
    ApplyMainStackLayout();
    LaunchClaudeInAllPanes();
}

private void LaunchClaudeInAllPanes()
{
    var surface = ViewModel.SelectedWorkspace?.SelectedSurface;
    if (surface == null) return;

    var cwd = ViewModel.SelectedWorkspace?.WorkingDirectory ?? "";
    var command = "claude --dangerously-skip-permissions --effort max --worktree";

    _ = Task.Run(async () =>
    {
        await Task.Delay(800); // Wait for all panes to initialize
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(cwd))
                surface.SendCommandToAllPanes($"cd \"{cwd}\"");

            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                Application.Current.Dispatcher.Invoke(() =>
                    surface.SendCommandToAllPanes(command));
            });
        });
    });
}
```

Also update the command palette entries (lines 1091-1094) to call these new methods instead of directly calling `ApplyLayout`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 5: Test manually**

Launch app. Click 2-col layout → both panes should run `claude --dangerously-skip-permissions --effort max --worktree`. Click SSH button → new pane with SSH command.

- [ ] **Step 6: Commit**

```bash
git add src/Cmux/Views/MainWindow.xaml src/Cmux/Views/MainWindow.xaml.cs
git commit -m "feat: add SSH/PowerShell toolbar buttons, layout buttons launch Claude Code"
```

---

## Task 9: Status indicators (tab dot + pane status bar)

**Files:**

- Modify: `src/Cmux/Controls/SurfaceTabBar.xaml`
- Modify: `src/Cmux/Controls/SurfaceTabBar.xaml.cs`
- Modify: `src/Cmux/Controls/TerminalControl.cs`

- [ ] **Step 1: Add status dot in SurfaceTabBar.xaml**

Find the tab item template in `SurfaceTabBar.xaml`. Add an `Ellipse` element next to each tab name:

```xml
<Ellipse x:Name="StatusDot" Width="8" Height="8" Margin="4,0,0,0"
         Visibility="Collapsed" />
```

- [ ] **Step 2: Update SurfaceTabBar.xaml.cs to bind status**

In `SurfaceTabBar.xaml.cs`, subscribe to `App.ClaudeStatusService.StatusChanged`:

```csharp
private void OnClaudeStatusChanged(string paneId, ClaudeStatus oldStatus, ClaudeStatus newStatus)
{
    Dispatcher.Invoke(() =>
    {
        // Find tab corresponding to this pane and update dot
        // Green = Working, Orange = WaitingForInput, Hidden = Idle
    });
}
```

Wire this in the constructor or loaded event.

- [ ] **Step 3: Add status bar overlay in TerminalControl.cs**

In `TerminalControl.cs`, add a status bar `TextBlock` overlay at the bottom of the control. Subscribe to `App.ClaudeStatusService.StatusChanged` in `AttachSession`:

```csharp
private TextBlock? _statusBar;

private void UpdateStatusBar(ClaudeStatus status)
{
    if (_statusBar == null) return;
    Dispatcher.Invoke(() =>
    {
        switch (status)
        {
            case ClaudeStatus.Working:
                _statusBar.Text = "Claude Code working...";
                _statusBar.Foreground = Brushes.LimeGreen;
                _statusBar.Visibility = Visibility.Visible;
                break;
            case ClaudeStatus.WaitingForInput:
                _statusBar.Text = "Claude Code waiting for input";
                _statusBar.Foreground = Brushes.Orange;
                _statusBar.Visibility = Visibility.Visible;
                break;
            default:
                _statusBar.Visibility = Visibility.Collapsed;
                break;
        }
    });
}
```

Add the `_statusBar` as a child visual in the control's visual tree.

- [ ] **Step 4: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/Cmux/Controls/SurfaceTabBar.xaml src/Cmux/Controls/SurfaceTabBar.xaml.cs src/Cmux/Controls/TerminalControl.cs
git commit -m "feat: add Claude Code status dot on tabs and status bar in panes"
```

---

## Task 10: Flash focused panel

**Files:**

- Modify: `src/Cmux/Controls/SplitPaneContainer.cs`

- [ ] **Step 1: Add flash border animation**

In `SplitPaneContainer.cs`, find where panes are built (the `BuildVisual` method around line 122). Each pane is wrapped in a `Border`. Add a second overlay `Border` for the flash effect:

```csharp
private void FlashPane(Border flashBorder, string accentColor)
{
    var color = !string.IsNullOrWhiteSpace(accentColor)
        ? (Color)ColorConverter.ConvertFromString(accentColor)
        : Colors.DodgerBlue;

    flashBorder.BorderBrush = new SolidColorBrush(color);
    flashBorder.BorderThickness = new Thickness(2);

    var animation = new DoubleAnimation
    {
        From = 1.0,
        To = 0.0,
        Duration = TimeSpan.FromMilliseconds(400),
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
    };

    animation.Completed += (_, _) =>
    {
        flashBorder.Opacity = 0;
        flashBorder.BorderThickness = new Thickness(0);
    };

    flashBorder.Opacity = 1;
    flashBorder.BeginAnimation(UIElement.OpacityProperty, animation);
}
```

Track `_flashBorders` dictionary keyed by paneId. When `FocusedPaneId` changes (already tracked in the surface DataContext), trigger `FlashPane` for the new pane.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Controls/SplitPaneContainer.cs
git commit -m "feat: add flash border animation on pane focus change"
```

---

## Task 11: Right-click "Open in Windows Explorer"

**Files:**

- Modify: `src/Cmux/Controls/TerminalControl.cs` (context menu section ~line 1321)

- [ ] **Step 1: Add context menu item**

In `TerminalControl.cs`, find where the `ContextMenu` is created. Add a new `MenuItem`:

```csharp
var openExplorerItem = new MenuItem { Header = "Open in Windows Explorer" };
openExplorerItem.Click += (_, _) =>
{
    var cwd = _session?.WorkingDirectory;
    if (!string.IsNullOrWhiteSpace(cwd) && System.IO.Directory.Exists(cwd))
        System.Diagnostics.Process.Start("explorer.exe", cwd);
};
contextMenu.Items.Add(new Separator());
contextMenu.Items.Add(openExplorerItem);
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Controls/TerminalControl.cs
git commit -m "feat: add 'Open in Windows Explorer' to terminal context menu"
```

---

## Task 12: Browser auto-open on dev server

**Files:**

- Modify: `src/Cmux/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Wire PortDetectionService to open browser**

In `MainWindow.xaml.cs`, in the constructor or loaded handler, add:

```csharp
App.PortDetectionService.DevServerStarted += (paneId, url) =>
{
    Dispatcher.Invoke(() =>
    {
        // Use existing BrowserControl infrastructure to open URL in split
        // The exact API depends on how BrowserControl is currently opened
        // Look for existing "open browser" logic and replicate with the detected URL
    });
};
```

The specifics depend on how `BrowserControl` is currently wired (check if there's an `OpenBrowser(url)` method on the surface or main window). Adapt accordingly.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Cmux/Views/MainWindow.xaml.cs
git commit -m "feat: auto-open browser when dev server starts"
```

---

## Task 13: Final build + smoke test

- [ ] **Step 1: Full build**

Run: `dotnet build Cmux.sln -c Debug`
Expected: 0 errors, 0 warnings (ideally)

- [ ] **Step 2: Run the app**

Run: `dotnet run --project src/Cmux/Cmux.csproj -c Debug`

Manual test checklist:

1. Template dropdown shows 6 projects → clicking one creates workspace with Claude Code
2. New workspace button opens folder picker
3. Layout buttons (2-col, grid, main+stack) launch Claude Code in each pane
4. SSH button opens pane with SSH command
5. PowerShell button opens new pane
6. Status dots appear when Claude Code runs (green = working, orange = waiting)
7. Status bar shows at bottom of pane
8. Flash border on pane focus switch
9. Right-click → "Open in Windows Explorer" works
10. Dev server URL auto-opens browser

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "chore: final adjustments after smoke testing"
```
