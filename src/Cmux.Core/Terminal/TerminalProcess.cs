using System.ComponentModel;
using System.Runtime.InteropServices;
using static Cmux.Core.Terminal.ConPtyInterop;

namespace Cmux.Core.Terminal;

/// <summary>
/// Manages a shell process attached to a ConPTY pseudo console.
/// </summary>
public sealed class TerminalProcess : IDisposable
{
    private readonly PROCESS_INFORMATION _processInfo;
    private IntPtr _attributeList;
    private bool _disposed;
    private readonly Thread _waitThread;

    public int ProcessId => _processInfo.dwProcessId;
    public IntPtr ProcessHandle => _processInfo.hProcess;

    public event Action? Exited;

    public TerminalProcess(PseudoConsole console, string? command = null, string? workingDirectory = null)
    {
        string shellCommand;
        if (command != null)
        {
            shellCommand = command;
        }
        else
        {
            var shellPath = DetectShell();
            // Wrap shell startup with chcp 65001 to set UTF-8 code page on the ConPTY.
            // Without this, ConPTY uses the OEM code page (850) and mangles UTF-8 output
            // (QR codes, Unicode block characters, etc.)
            if (shellPath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                shellCommand = "cmd.exe /k chcp 65001 > nul";
            else
                shellCommand = $"cmd.exe /c chcp 65001 > nul && \"{shellPath}\"";
        }

        // Build environment block
        var envBlock = BuildUtf8Environment();

        // Initialize thread attribute list for ConPTY
        _attributeList = CreateAttributeList(console.Handle);

        // Create process with ConPTY
        var startupInfo = new STARTUPINFOEX
        {
            lpAttributeList = _attributeList,
        };
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        bool success = CreateProcess(
            null,
            shellCommand,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            envBlock,
            workingDirectory,
            ref startupInfo,
            out _processInfo);

        if (envBlock != IntPtr.Zero)
            Marshal.FreeHGlobal(envBlock);

        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process with ConPTY.");

        // Start a background thread to wait for process exit
        _waitThread = new Thread(WaitForExitThread)
        {
            IsBackground = true,
            Name = $"ConPTY-Wait-{_processInfo.dwProcessId}",
        };
        _waitThread.Start();
    }

    /// <summary>
    /// Detects the best available shell on the system.
    /// Priority: pwsh.exe > powershell.exe > cmd.exe
    /// </summary>
    private static string DetectShell()
    {
        // Check for PowerShell 7+ (pwsh)
        var pwshPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            "pwsh.exe",
        };

        foreach (var path in pwshPaths)
        {
            if (path == "pwsh.exe")
            {
                // Check if it's in PATH
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
            else if (File.Exists(path))
            {
                return path;
            }
        }

        // Fall back to Windows PowerShell
        var winPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPowerShell))
            return winPowerShell;

        // Last resort: cmd.exe from COMSPEC
        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrEmpty(comspec) && File.Exists(comspec))
            return comspec;

        return "cmd.exe";
    }

    /// <summary>
    /// Builds a Unicode environment block with UTF-8 code page settings.
    /// </summary>
    private static IntPtr BuildUtf8Environment()
    {
        // Get current environment and add/override UTF-8 settings
        var env = Environment.GetEnvironmentVariables();
        env["PYTHONIOENCODING"] = "utf-8";
        // Signal to .NET console apps to use UTF-8
        env["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";
        // Remove terminal-identity env vars so apps (Expo, etc.) don't use
        // sextant chars (U+1FB00+) which our VtParser can't handle (BMP only).
        env.Remove("WT_SESSION");
        env.Remove("TERM_PROGRAM");
        env.Remove("KITTY_WINDOW_ID");
        env.Remove("ALACRITTY_WINDOW_ID");

        // Build null-terminated Unicode environment block: "KEY=VALUE\0KEY=VALUE\0\0"
        var sb = new System.Text.StringBuilder();
        foreach (System.Collections.DictionaryEntry entry in env)
        {
            sb.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
        }
        sb.Append('\0'); // Double null terminator

        var bytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static IntPtr CreateAttributeList(IntPtr conPtyHandle)
    {
        // Query the required size
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var attributeList = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(
            attributeList,
            0,
            (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            conPtyHandle,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        return attributeList;
    }

    private void WaitForExitThread()
    {
        WaitForSingleObject(_processInfo.hProcess, INFINITE);
        Exited?.Invoke();
    }

    public void WaitForExit()
    {
        WaitForSingleObject(_processInfo.hProcess, INFINITE);
    }

    public bool HasExited
    {
        get
        {
            if (!GetExitCodeProcess(_processInfo.hProcess, out uint exitCode))
                return true;
            return exitCode != STILL_ACTIVE;
        }
    }

    public void Kill()
    {
        if (!_disposed && !HasExited)
        {
            TerminateProcess(_processInfo.hProcess, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Kill();

        if (_processInfo.hProcess != IntPtr.Zero)
            CloseHandle(_processInfo.hProcess);
        if (_processInfo.hThread != IntPtr.Zero)
            CloseHandle(_processInfo.hThread);

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }
}
