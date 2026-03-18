using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cmux.Core.Models;
using Cmux.Core.Terminal;

namespace Cmux.Core.Services;

/// <summary>
/// Detects Claude Code status per workspace using triple-signal hybrid:
/// 1. Hook files (PreToolUse → "working", Stop → "waiting") keyed by MD5 hash of CWD
/// 2. BEL character (0x07) emitted by Claude Code when finished
/// 3. Sustained output (>500 byte chunks) as secondary confirmation
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
    public event Action<string, string>? SessionIdCaptured;

    [GeneratedRegex(@"session_[A-Za-z0-9]{20,}")]
    private static partial Regex SessionIdRegex();

    public ClaudeCodeStatusService()
    {
        Directory.CreateDirectory(StatusDir);
        CleanupStaleFiles();
        _pollTimer = new System.Timers.Timer(2000);
        _pollTimer.Elapsed += (_, _) => PollStatus();
        _pollTimer.Start();
    }

    // --- Public API ---

    public void SetPaneWorkingDirectory(string paneId, string? cwd)
    {
        if (_paneStates.TryGetValue(paneId, out var state))
            state.FallbackCwd = cwd;
    }

    public void MarkAsClaudeCode(string paneId)
    {
        if (_paneStates.TryGetValue(paneId, out var state))
            state.HasClaudeCode = true;
    }

    public bool IsClaudeCode(string paneId)
    {
        return _paneStates.TryGetValue(paneId, out var state) && state.HasClaudeCode;
    }

    public ClaudeStatus GetStatus(string paneId)
    {
        return _paneStates.TryGetValue(paneId, out var state) ? state.Status : ClaudeStatus.Idle;
    }

    public string? GetSessionId(string paneId)
    {
        return _paneStates.TryGetValue(paneId, out var state) ? state.SessionId : null;
    }

    /// <summary>Handle BEL from daemon mode (paneId-based).</summary>
    public void HandleDaemonBell(string paneId)
    {
        if (_paneStates.TryGetValue(paneId, out var state) && state.HasClaudeCode)
        {
            state.LastBellUtc = DateTime.UtcNow;
            TransitionTo(paneId, state, ClaudeStatus.WaitingForInput);
        }
    }

    // --- Registration ---

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

            // Capture session ID
            if (state.HasClaudeCode && state.SessionId == null)
            {
                var match = SessionIdRegex().Match(text);
                if (match.Success)
                {
                    state.SessionId = match.Value;
                    SessionIdCaptured?.Invoke(paneId, match.Value);
                }
            }

            // Signal 3: Sustained output tracking (chunks > 500 bytes)
            if (state.HasClaudeCode && data.Length >= 500)
            {
                var now = DateTime.UtcNow;
                if ((now - state.LastLargeChunkUtc).TotalSeconds > 3)
                    state.LargeChunkCount = 0;
                state.LastLargeChunkUtc = now;
                state.LargeChunkCount++;
            }
        };

        // Signal 2: BEL detection (local mode)
        session.BellReceived += () =>
        {
            if (state.HasClaudeCode)
            {
                state.LastBellUtc = DateTime.UtcNow;
                TransitionTo(paneId, state, ClaudeStatus.WaitingForInput);
            }
        };
    }

    public void UnregisterPane(string paneId)
    {
        if (_paneStates.TryRemove(paneId, out var state))
        {
            // Clean up status file
            var cwd = state.Session.WorkingDirectory ?? state.FallbackCwd;
            if (!string.IsNullOrEmpty(cwd))
            {
                try
                {
                    var file = Path.Combine(StatusDir, $"{CwdToHash(cwd)}.txt");
                    if (File.Exists(file)) File.Delete(file);
                }
                catch { }
            }
        }
    }

    // --- Polling ---

    private void PollStatus()
    {
        if (_disposed) return;

        // Read all status files
        var statusByHash = new Dictionary<string, (string status, DateTime timestamp)>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(StatusDir))
            {
                foreach (var file in Directory.GetFiles(StatusDir, "*.txt"))
                {
                    var hash = Path.GetFileNameWithoutExtension(file);
                    var content = File.ReadAllText(file).Trim();

                    // Parse "status|timestamp" format, fallback to legacy "status" only
                    string hookStatus;
                    DateTime hookTimestamp;
                    var pipeIndex = content.IndexOf('|');
                    if (pipeIndex >= 0)
                    {
                        hookStatus = content[..pipeIndex].ToLowerInvariant();
                        DateTimeOffset.TryParse(content[(pipeIndex + 1)..], out var dto);
                        hookTimestamp = dto.UtcDateTime;
                    }
                    else
                    {
                        hookStatus = content.ToLowerInvariant();
                        hookTimestamp = File.GetLastWriteTimeUtc(file);
                    }

                    statusByHash[hash] = (hookStatus, hookTimestamp);
                }
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        foreach (var (paneId, state) in _paneStates)
        {
            if (!state.HasClaudeCode)
            {
                if (state.Status != ClaudeStatus.Idle)
                    TransitionTo(paneId, state, ClaudeStatus.Idle);
                continue;
            }

            var cwd = state.Session.WorkingDirectory ?? state.FallbackCwd;
            if (string.IsNullOrEmpty(cwd)) continue;

            var hash = CwdToHash(cwd);

            // Read hook signal
            string? hookStatus = null;
            double hookAgeSec = double.MaxValue;
            if (statusByHash.TryGetValue(hash, out var hookData))
            {
                hookStatus = hookData.status;
                hookAgeSec = (now - hookData.timestamp).TotalSeconds;
            }

            var bellAgeSec = (now - state.LastBellUtc).TotalSeconds;
            var hasSustainedOutput = state.LargeChunkCount >= 3
                && (now - state.LastLargeChunkUtc).TotalSeconds < 3;

            // State machine
            var newStatus = state.Status;
            switch (state.Status)
            {
                case ClaudeStatus.Idle:
                    if (hookStatus == "working" && hookAgeSec < 120)
                        newStatus = ClaudeStatus.Working;
                    break;

                case ClaudeStatus.Working:
                    if (bellAgeSec < 5)
                        newStatus = ClaudeStatus.WaitingForInput;
                    else if (hookStatus == "waiting" && hookAgeSec < 120)
                        newStatus = ClaudeStatus.WaitingForInput;
                    else if (hookAgeSec > 120 && !hasSustainedOutput)
                        newStatus = ClaudeStatus.Idle;
                    break;

                case ClaudeStatus.WaitingForInput:
                    if (hookStatus == "working" && hookAgeSec < 10)
                        newStatus = ClaudeStatus.Working;
                    else if (hookAgeSec > 120 && bellAgeSec > 120)
                        newStatus = ClaudeStatus.Idle;
                    break;
            }

            TransitionTo(paneId, state, newStatus);
        }
    }

    // --- Helpers ---

    private static string CwdToHash(string cwd)
    {
        var normalized = cwd.TrimEnd('/', '\\').ToLowerInvariant();
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    private void CleanupStaleFiles()
    {
        try
        {
            if (!Directory.Exists(StatusDir)) return;
            var cutoff = DateTime.UtcNow.AddHours(-2);
            foreach (var file in Directory.GetFiles(StatusDir, "*.txt"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
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
        public bool HasClaudeCode { get; set; }
        public string? SessionId { get; set; }
        public string? FallbackCwd { get; set; }
        public DateTime LastBellUtc { get; set; } = DateTime.MinValue;
        public DateTime LastLargeChunkUtc { get; set; } = DateTime.MinValue;
        public int LargeChunkCount { get; set; }
    }
}
