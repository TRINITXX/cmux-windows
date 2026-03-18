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
        _pollTimer.Elapsed += (_, _) => PollStatus();
        _pollTimer.Start();
    }

    public void RegisterPane(string paneId, TerminalSession session)
    {
        var state = new PaneState { Session = session };
        _paneStates[paneId] = state;

        session.RawOutputReceived += _ =>
        {
            state.LastOutputTime = DateTime.UtcNow;
            // If we were waiting for input and output resumes, Claude is working again
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

    private void PollStatus()
    {
        if (_disposed) return;

        // Check if any claude process exists globally (works in daemon mode)
        bool claudeRunningGlobally = false;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("claude");
            claudeRunningGlobally = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
        }
        catch { }

        var now = DateTime.UtcNow;
        foreach (var (paneId, state) in _paneStates)
        {
            var outputAge = (now - state.LastOutputTime).TotalSeconds;
            var notifAge = state.NotificationTime.HasValue
                ? (now - state.NotificationTime.Value).TotalSeconds
                : double.MaxValue;

            // Detect if this pane has Claude Code running based on output activity
            var hasRecentActivity = outputAge < 30; // Had output in last 30s = likely has claude

            if (!claudeRunningGlobally || !hasRecentActivity)
            {
                TransitionTo(paneId, state, ClaudeStatus.Idle);
                continue;
            }

            // Claude is running and this pane has recent activity
            if (notifAge < 60 && outputAge > 2)
            {
                // Got a notification/BEL and output stopped = waiting for input
                TransitionTo(paneId, state, ClaudeStatus.WaitingForInput);
            }
            else if (outputAge < 3)
            {
                // Output flowing = working
                TransitionTo(paneId, state, ClaudeStatus.Working);
            }
            // else: keep current state to avoid flicker
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
