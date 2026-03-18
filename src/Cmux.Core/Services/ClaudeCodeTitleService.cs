using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cmux.Core.Services;

/// <summary>
/// Generates pane titles for Claude Code sessions using Haiku via the claude CLI.
/// Calls Haiku on the first user message, then re-evaluates every 10 short messages.
/// </summary>
public class ClaudeCodeTitleService
{
    private readonly ConcurrentDictionary<string, PaneTitleState> _states = new();

    /// <summary>Fired when a title is generated. Args: paneId, newTitle.</summary>
    public event Action<string, string>? TitleGenerated;

    public void RegisterPane(string paneId)
    {
        _states[paneId] = new PaneTitleState();
    }

    public void UnregisterPane(string paneId)
    {
        _states.TryRemove(paneId, out _);
    }

    /// <summary>
    /// Called when the user submits a command/message in a pane.
    /// </summary>
    public void OnUserMessage(string paneId, string message)
    {
        if (!_states.TryGetValue(paneId, out var state)) return;

        // Ignore messages > 10 lines (likely pasted errors/logs)
        var lineCount = message.Count(c => c == '\n') + 1;
        if (lineCount > 10) return;

        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        state.UserMessages.Add(trimmed);

        if (!state.HasGeneratedFirstTitle)
        {
            state.HasGeneratedFirstTitle = true;
            _ = GenerateTitleAsync(paneId, state, isFirstMessage: true);
        }
        else if (state.UserMessages.Count >= 10)
        {
            _ = GenerateTitleAsync(paneId, state, isFirstMessage: false);
        }
    }

    private async Task GenerateTitleAsync(string paneId, PaneTitleState state, bool isFirstMessage)
    {
        string prompt;
        if (isFirstMessage)
        {
            var msg = state.UserMessages.LastOrDefault() ?? "";
            prompt = $"Generate a 3-5 word tab title (no quotes, no explanation, no punctuation) for this coding task: {msg}";
        }
        else
        {
            var currentTitle = state.CurrentTitle ?? "Claude Code";
            var messages = string.Join("\n", state.UserMessages.Select((m, i) => $"{i + 1}. {m}"));
            prompt = $"Current tab title: '{currentTitle}'. Here are the user's last {state.UserMessages.Count} messages to Claude Code:\n{messages}\nShould the title change? Reply with ONLY the new title (3-5 words, no quotes) or KEEP if unchanged.";
            state.UserMessages.Clear();
        }

        try
        {
            var result = await CallHaikuAsync(prompt);
            if (string.IsNullOrWhiteSpace(result)) return;

            result = result.Trim().Trim('"', '\'');
            if (result.Equals("KEEP", StringComparison.OrdinalIgnoreCase)) return;
            if (result.Length > 40) result = result[..40];

            state.CurrentTitle = result;
            TitleGenerated?.Invoke(paneId, result);
        }
        catch { }
    }

    private static async Task<string> CallHaikuAsync(string prompt)
    {
        var escaped = prompt
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = $"--model haiku -p \"{escaped}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output.Trim();
    }

    private class PaneTitleState
    {
        public bool HasGeneratedFirstTitle { get; set; }
        public string? CurrentTitle { get; set; }
        public List<string> UserMessages { get; } = [];
    }
}
