using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;

namespace NpuChat;

public sealed partial class App : Application
{
    // Windows closes this handle at process exit, including an abrupt exit.
    private static readonly SafeFileHandle? WorkerJob = WorkerLifetime.BindToApp();
    private MainWindow? window;

    public App()
    {
        DiagnosticLog.Start();
        DiagnosticLog.Write("app", $"Starting PID {Environment.ProcessId}; OS {Environment.OSVersion.Version}; " +
            $".NET {Environment.Version}; worker job attached: {WorkerJob is not null}; binary {AppContext.BaseDirectory}");
        UnhandledException += (_, args) => DiagnosticLog.Write("winui exception", args.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLog.Write("unhandled exception", args.ExceptionObject is Exception error ? error.ToString() : "Unknown exception");
        TaskScheduler.UnobservedTaskException += (_, args) => DiagnosticLog.Write("task exception", args.Exception.ToString());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DiagnosticLog.Write("app", "Process exiting");
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
        GC.KeepAlive(WorkerJob);
    }
}
