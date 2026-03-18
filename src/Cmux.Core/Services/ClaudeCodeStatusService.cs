using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cmux.Core.Models;
using Cmux.Core.Terminal;

namespace Cmux.Core.Services;

/// <summary>
/// Detects Claude Code status per workspace by reading hook-written status files.
/// Files are at %LOCALAPPDATA%/cmux/claude-status/{folderName}.txt
/// containing "working" or "waiting".
/// Also detects Claude Code presence by scanning terminal output for the banner.
/// </summary>
public partial class ClaudeCodeStatusService : IDisposable
{
    private static readonly string StatusDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux", "claude-status");

    private readonly ConcurrentDictionary<string, PaneState> _paneStates = new();
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    public event Action<string, ClaudeStatus, ClaudeStatus>? StatusChanged;
    public event Action<string>? ClaudeCodeDetected;
    public event Action<string, string>? SessionIdCaptured; // paneId, sessionId

    [GeneratedRegex(@"session_[A-Za-z0-9]{20,}")]
    private static partial Regex SessionIdRegex();

    public ClaudeCodeStatusService()
    {
        Directory.CreateDirectory(StatusDir);
        _pollTimer = new System.Timers.Timer(2000);
        _pollTimer.Elapsed += (_, _) => PollStatus();
        _pollTimer.Start();
    }

    public void SetPaneWorkingDirectory(string paneId, string? cwd)
    {
        if (_paneStates.TryGetValue(paneId, out var state))
            state.FallbackCwd = cwd;
    }

    public void RegisterPane(string paneId, TerminalSession session)
    {
        var state = new PaneState { Session = session };
        _paneStates[paneId] = state;

        session.RawOutputReceived += data =>
        {
            var text = Encoding.UTF8.GetString(data);

            // Detect Claude Code by scanning output for its banner
            if (!state.HasClaudeCode)
            {
                if (text.Contains("Claude Code", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("/remote-control", StringComparison.OrdinalIgnoreCase))
                {
                    state.HasClaudeCode = true;
                    ClaudeCodeDetected?.Invoke(paneId);
                }
            }

            // Capture session ID from output (e.g. "session_01FeT5eLWJBuTZrEgrTBdvmT")
            if (state.HasClaudeCode && state.SessionId == null)
            {
                var match = SessionIdRegex().Match(text);
                if (match.Success)
                {
                    state.SessionId = match.Value;
                    SessionIdCaptured?.Invoke(paneId, match.Value);
                }
            }
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

        // Read all status files once
        var statusByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(StatusDir))
            {
                foreach (var file in Directory.GetFiles(StatusDir, "*.txt"))
                {
                    var folderName = Path.GetFileNameWithoutExtension(file);
                    var content = File.ReadAllText(file).Trim().ToLowerInvariant();
                    statusByFolder[folderName] = content;
                }
            }
        }
        catch { }

        foreach (var (paneId, state) in _paneStates)
        {
            if (!state.HasClaudeCode)
            {
                if (state.Status != ClaudeStatus.Idle)
                    TransitionTo(paneId, state, ClaudeStatus.Idle);
                continue;
            }

            // Match pane to status file by workspace folder name
            var cwd = state.Session.WorkingDirectory ?? state.FallbackCwd;
            if (string.IsNullOrEmpty(cwd)) continue;

            var folderName = Path.GetFileName(cwd.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(folderName)) continue;

            if (statusByFolder.TryGetValue(folderName, out var hookStatus))
            {
                var newStatus = hookStatus switch
                {
                    "working" => ClaudeStatus.Working,
                    "waiting" => ClaudeStatus.WaitingForInput,
                    _ => ClaudeStatus.Idle
                };
                TransitionTo(paneId, state, newStatus);
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

    public string? GetSessionId(string paneId)
    {
        return _paneStates.TryGetValue(paneId, out var state) ? state.SessionId : null;
    }

    private class PaneState
    {
        public TerminalSession Session { get; init; } = null!;
        public ClaudeStatus Status { get; set; } = ClaudeStatus.Idle;
        public bool HasClaudeCode { get; set; }
        public string? SessionId { get; set; }
        public string? FallbackCwd { get; set; }
    }
}
