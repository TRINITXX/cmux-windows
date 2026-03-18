using System.Collections.Concurrent;
using System.Text;
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

        session.RawOutputReceived += data =>
        {
            // Detect Claude Code by scanning output for its banner
            if (!state.HasClaudeCode)
            {
                var text = Encoding.UTF8.GetString(data);
                if (text.Contains("Claude Code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("claude-code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("/remote-control", StringComparison.OrdinalIgnoreCase))
                {
                    state.HasClaudeCode = true;
                }
            }

            // Only count substantial output as "working" (ignore cursor moves, redraws)
            // Small bursts (<50 bytes) are usually terminal UI updates, not Claude generating
            if (data.Length > 50)
            {
                state.LastBigOutputTime = DateTime.UtcNow;
                state.BigOutputCount++;
            }

            if (state.HasClaudeCode && state.Status == ClaudeStatus.WaitingForInput && data.Length > 50)
                TransitionTo(paneId, state, ClaudeStatus.Working);
        };

        session.ProcessExited += () =>
        {
            state.HasClaudeCode = false;
            TransitionTo(paneId, state, ClaudeStatus.Idle);
        };
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

        var now = DateTime.UtcNow;
        foreach (var (paneId, state) in _paneStates)
        {
            // Only track panes where Claude Code was detected
            if (!state.HasClaudeCode)
            {
                if (state.Status != ClaudeStatus.Idle)
                    TransitionTo(paneId, state, ClaudeStatus.Idle);
                continue;
            }

            var bigOutputAge = (now - state.LastBigOutputTime).TotalSeconds;

            if (bigOutputAge < 5)
            {
                // Substantial output in the last 5s = Claude is generating
                TransitionTo(paneId, state, ClaudeStatus.Working);
            }
            else if (bigOutputAge >= 5 && bigOutputAge < 120)
            {
                // No substantial output for 5+ seconds = waiting for input
                TransitionTo(paneId, state, ClaudeStatus.WaitingForInput);
            }
            else if (bigOutputAge >= 120)
            {
                // No output for 2+ minutes — Claude probably exited
                state.HasClaudeCode = false;
                TransitionTo(paneId, state, ClaudeStatus.Idle);
            }
        }
    }

    private void TransitionTo(string paneId, PaneState state, ClaudeStatus newStatus)
    {
        var old = state.Status;
        if (old == newStatus) return;
        state.Status = newStatus;
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
        public DateTime LastBigOutputTime { get; set; } = DateTime.MinValue;
        public long BigOutputCount { get; set; }
        public bool HasClaudeCode { get; set; }
    }
}
