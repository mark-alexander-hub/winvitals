using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WinVitals.Core;

namespace WinVitals.App;

public partial class App : Application
{
    /// <summary>True when this process has administrator rights.</summary>
    public static bool Elevated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // One executable, two personalities: arguments mean the command-line scan,
        // no arguments means the interface. --gui asks for the interface explicitly,
        // which is the only way to combine it with --no-elevate.
        var wantsGui = e.Args.Length == 0
                       || e.Args.Any(a => a.Equals("--gui", StringComparison.OrdinalIgnoreCase));

        var skipElevation = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

        if (!wantsGui)
        {
            ConsoleHelper.Attach();
            int code;
            try { code = Cli.Run(e.Args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WinVitals failed: {ex.Message}");
                code = 4;
            }
            ConsoleHelper.Detach();
            Shutdown(code);
            return;
        }

        base.OnStartup(e);

        Elevated = ScanContext.IsElevated();

        // Repairs need administrator rights, and so do several of the checks. Ask once,
        // at the start. Declining is respected: the app runs with a banner explaining
        // what is unavailable rather than refusing to start.
        if (!Elevated && !skipElevation && TryRelaunchElevated())
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        Theme.Apply(this);

        // --run <check> opens straight into one check, so a shortcut or a scheduled
        // task can launch "the sleep one" without the user picking from the list.
        var runIndex = Array.FindIndex(e.Args, a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        var startWith = runIndex >= 0 && runIndex + 1 < e.Args.Length ? e.Args[runIndex + 1] : null;

        new MainWindow(startWith).Show();
    }

    private static bool TryRelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            };
            return Process.Start(psi) is not null;
        }
        catch
        {
            // The user said no to the UAC prompt. Carry on unelevated.
            return false;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"WinVitals hit an unexpected problem and had to stop what it was doing.\n\n{e.Exception.Message}\n\n"
            + "Nothing further was changed. Please report this so the check can be made more robust.",
            "WinVitals", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
