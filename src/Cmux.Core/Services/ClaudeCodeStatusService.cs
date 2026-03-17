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
