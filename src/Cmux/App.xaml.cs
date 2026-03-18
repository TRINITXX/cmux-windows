using System.IO;
using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;
using Cmux.Services;

namespace Cmux;

public partial class App : Application
{
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }
    public static SnippetService SnippetService { get; } = new();
    public static CommandLogService CommandLogService { get; } = new();
    public static ClaudeCodeStatusService ClaudeStatusService { get; } = new();
    public static PortDetectionService PortDetectionService { get; } = new();
    public static WorkspaceTemplateService TemplateService { get; } = new();
    public static ClaudeCodeTitleService TitleService { get; } = new();
    public static DaemonClient DaemonClient { get; } = new();
    public static Task<bool> DaemonConnectTask { get; private set; } = Task.FromResult(false);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Force UTF-8 code page for ConPTY (fixes QR codes, Unicode block chars)
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);

        // Add global exception handlers to diagnose crashes
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRASH] DispatcherUnhandledException: {args.Exception}");
            System.Windows.MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            System.Diagnostics.Debug.WriteLine($"[CRASH] UnhandledException: {ex}");
            System.Windows.MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Start the named pipe server for CLI communication
        _pipeServer = new NamedPipeServer();
        PipeServer = _pipeServer;
        _pipeServer.Start();

        // Daemon connect: try existing daemon first, then start one if needed.
        // Sessions wait for this task before deciding local vs daemon mode.
        DaemonConnectTask = Task.Run(() =>
        {
            DaemonLog("[App] Phase 1: Quick daemon check (300ms)...");
            if (DaemonClient.TryConnect(300))
            {
                DaemonLog("[App] Phase 1: Daemon connected!");
                DaemonClient.RaiseConnected();
                return true;
            }
            DaemonLog("[App] Phase 1: Daemon not available, starting daemon...");

            var connected = DaemonClient.StartDaemonAndConnect();
            DaemonLog(connected
                ? "[App] Phase 2: Daemon started and connected"
                : "[App] Phase 2: Daemon failed to start");
            if (connected) DaemonClient.RaiseConnected();
            return connected;
        });

        // Forward daemon BEL events to status service (for daemon-mode detection)
        DaemonClient.BellReceived += paneId => ClaudeStatusService.HandleDaemonBell(paneId);

        // Wire up Windows toast notifications
        NotificationService.NotificationAdded += notification =>
        {
            // Only show toast when the app window is not focused
            var mainWindow = Current.MainWindow;
            if (mainWindow != null && !mainWindow.IsActive)
            {
                var workspaceName = "Terminal"; // Will be enriched by MainViewModel
                Services.ToastNotificationHelper.ShowToast(notification, workspaceName);
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        DaemonClient.Dispose();
        ClaudeStatusService.Dispose();
        base.OnExit(e);
    }

    internal static void DaemonLog(string message) => DaemonClient.LogDaemon(message);
}
