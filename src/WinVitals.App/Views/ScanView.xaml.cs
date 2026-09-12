using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;

namespace WinVitals.App.Views;

public partial class ScanView : UserControl
{
    private readonly Playbook _playbook;

    public ScanView(Playbook playbook)
    {
        InitializeComponent();
        _playbook = playbook;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var collectors = AppInfo.CollectorsFor(_playbook);
        var total = Math.Max(collectors.Count, 1);
        var completed = 0;
        var finished = new List<string>();

        var ctx = new ScanContext
        {
            Elevated = App.Elevated,
            // The interface always redacts nothing: this report stays on the machine.
            // Sharing goes through the explicit "save a shareable copy" action, which
            // redacts.
            Redactor = new Redactor(false),
            Progress = name => Dispatcher.Invoke(() =>
            {
                StatusText.Text = name.TrimEnd('.', ' ');
                DetailText.Text = $"Check {Math.Min(completed + 1, total)} of {total}. Nothing is being changed.";

                if (finished.Count > 0)
                {
                    DoneList.Visibility = Visibility.Visible;
                    DoneList.Text = "Done: " + string.Join(", ", finished);
                }

                completed++;
                Progress.Value = (completed - 1) * 100.0 / total;
                finished.Add(name.TrimEnd('.', ' '));
            }),
        };

        // The collectors are synchronous and some of them shell out for several
        // seconds, so they run off the UI thread.
        var scan = await Task.Run(() => new ScanRunner(collectors).Run(ctx, AppInfo.Version, redacted: false));

        Progress.Value = 100;
        StatusText.Text = "Finished";

        MainWindow.Instance?.Navigate(new ResultsView(scan, _playbook),
            "What I found", Summarise(scan));
    }

    private static string Summarise(ScanResult scan)
    {
        var needsAction = scan.Count(Severity.Critical) + scan.Count(Severity.Warning)
                          + scan.Count(Severity.Advisory);

        if (needsAction == 0)
            return "Nothing needs attention. Every check that ran came back healthy.";

        var parts = new List<string>();
        if (scan.Count(Severity.Critical) > 0) parts.Add($"{scan.Count(Severity.Critical)} critical");
        if (scan.Count(Severity.Warning) > 0) parts.Add($"{scan.Count(Severity.Warning)} needing attention");
        if (scan.Count(Severity.Advisory) > 0) parts.Add($"{scan.Count(Severity.Advisory)} worth knowing about");

        return string.Join(", ", parts) + $" — checked in {Seconds(scan.Duration)}.";
    }

    private static string Seconds(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        if (seconds < 1) return "under a second";
        return Math.Abs(seconds - 1) < 0.05 ? "1 second" : $"{seconds:0.#} seconds";
    }
}
