using System.Windows;
using System.Windows.Threading;
using WinVitals.Core;

namespace WinVitals.App;

public partial class App : Application
{
    /// <summary>
    /// True when this process has administrator rights. The manifest requires it, so
    /// this is only ever false when the binary is launched in a way that bypasses the
    /// manifest (CreateProcess from an unelevated parent, which fails with error 740
    /// before we get here, or a debugger).
    /// </summary>
    public static bool Elevated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Every unhandled failure path lands in the log before anything else happens.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception (process will terminate)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += OnUnhandledException;
        Exit += (_, args) => Log.Info($"Exit with code {args.ApplicationExitCode}");

        Log.Info($"WinVitals {AppInfo.Version} starting; args: [{string.Join(" ", e.Args)}]");

        // One executable, two personalities: arguments mean the command-line scan,
        // no arguments means the interface. --gui asks for the interface explicitly.
        var wantsGui = e.Args.Length == 0
                       || e.Args.Any(a => a.Equals("--gui", StringComparison.OrdinalIgnoreCase));

        if (!wantsGui)
        {
            ConsoleHelper.Attach();
            int code;
            try { code = Cli.Run(e.Args); }
            catch (Exception ex)
            {
                Log.Error("CLI failed", ex);
                Console.Error.WriteLine($"WinVitals failed: {ex.Message}");
                code = 4;
            }
            ConsoleHelper.Detach();
            Shutdown(code);
            return;
        }

        base.OnStartup(e);

        Elevated = ScanContext.IsElevated();
        Log.Info($"Elevated: {Elevated}");

        // Nothing closes the app except the user closing the main window. Without
        // this, WPF's default OnLastWindowClose can end the process when a modal
        // dialog is the only window briefly alive during a navigation.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        Theme.Apply(this);

        // --run <check> opens straight into one check, so a shortcut or a scheduled
        // task can launch "the sleep one" without the user picking from the list.
        var runIndex = Array.FindIndex(e.Args, a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        var startWith = runIndex >= 0 && runIndex + 1 < e.Args.Length ? e.Args[runIndex + 1] : null;

        var window = new MainWindow(startWith);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread", e.Exception);

        MessageBox.Show(
            $"WinVitals hit an unexpected problem and had to stop what it was doing.\n\n{e.Exception.Message}\n\n"
            + $"Nothing further was changed. The details were written to:\n{Log.Path_}",
            "WinVitals", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
