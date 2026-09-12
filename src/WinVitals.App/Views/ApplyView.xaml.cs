using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

public partial class ApplyView : UserControl
{
    private readonly List<(Finding Finding, IFix Fix)> _work;

    /// <summary>Null when the repair was started from the tools screen rather than from a scan.</summary>
    private readonly ScanResult? _scan;

    private readonly Playbook _playbook;
    private RepairSession? _session;

    public ApplyView(List<(Finding Finding, IFix Fix)> work, ScanResult? scan, Playbook playbook)
    {
        InitializeComponent();
        _work = work;
        _scan = scan;
        _playbook = playbook;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var session = new RepairSession(AppInfo.Version, App.Elevated);
        _session = session;

        // --- Safety net ---------------------------------------------------
        StatusText.Text = "Creating a restore point";
        DetailText.Text = "This takes a few seconds. Nothing has been changed yet.";

        var safety = await Task.Run(() => session.PrepareSafetyNet(_playbook.Title));
        StepHost.Children.Add(SafetyCard(safety));

        if (!safety.Created && !ConfirmWithoutSafetyNet(safety))
        {
            StatusText.Text = "Cancelled";
            DetailText.Text = "Nothing was changed.";
            Progress.Value = 0;
            FooterButtons.Visibility = Visibility.Visible;
            UndoButton.Visibility = Visibility.Collapsed;
            return;
        }

        // --- Apply ----------------------------------------------------------
        var total = Math.Max(_work.Count, 1);
        var done = 0;

        foreach (var (finding, fix) in _work)
        {
            StatusText.Text = fix.Title;
            DetailText.Text = fix.NeedsRestart
                ? "This one needs a restart afterwards."
                : "Working...";

            var result = await Task.Run(() => session.Apply(fix, finding,
                message => Dispatcher.Invoke(() => DetailText.Text = message)));

            StepHost.Children.Add(ResultCard(result));

            done++;
            Progress.Value = done * 100.0 / total;
        }

        Finish(session);
    }

    private bool ConfirmWithoutSafetyNet(RestorePointOutcome safety)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{safety.Summary}.\n\n{safety.Detail}\n\n"
            + "You can still continue. Every change WinVitals is about to make is individually reversible "
            + "from the \"Undo previous changes\" screen — a restore point is a second layer of safety, not "
            + "the only one.\n\n"
            + "Continue without a restore point?",
            "No restore point", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private void Finish(RepairSession session)
    {
        Progress.Value = 100;

        var ok = session.Succeeded;
        var failed = session.Failed;

        StatusText.Text = failed == 0
            ? ok == 1 ? "Done — one change applied" : $"Done — {ok} changes applied"
            : $"Finished with {failed} problem(s)";

        DetailText.Text = session.RestartNeeded
            ? "Restart the machine to finish applying these changes."
            : "You can undo any of this from the Undo screen at any time.";

        FooterButtons.Visibility = Visibility.Visible;
        RestartButton.Visibility = session.RestartNeeded ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.Visibility = session.Journal.Session.Entries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        MainWindow.Instance?.Navigate(this, "Repairs finished", StatusText.Text);
    }

    private static Border SafetyCard(RestorePointOutcome safety)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var pill = Ui.Pill(safety.Created ? "Verified" : "No safety net", safety.Created ? "Ok" : "Warning");
        pill.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(pill);

        var title = Ui.Text(safety.Summary, "H2");
        title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);

        panel.Children.Add(header);
        panel.Children.Add(Ui.Text(safety.Detail, "Muted", new Thickness(0, 8, 0, 0)));

        var card = Ui.Card(panel, new Thickness(0, 0, 0, 10));
        card.BorderThickness = new Thickness(4, 1, 1, 1);
        card.BorderBrush = Ui.Brush(safety.Created ? "Ok" : "Warning");
        return card;
    }

    private static Border ResultCard(AppliedFix result)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var pill = Ui.Pill(result.Success ? "Applied" : "Failed", result.Success ? "Ok" : "Critical");
        pill.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(pill);

        var title = Ui.Text(result.Title, "H2");
        title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);

        panel.Children.Add(header);
        panel.Children.Add(Ui.Text(result.Message, "Body", new Thickness(0, 8, 0, 0)));

        var card = Ui.Card(panel, new Thickness(0, 0, 0, 10));
        card.BorderThickness = new Thickness(4, 1, 1, 1);
        card.BorderBrush = Ui.Brush(result.Success ? "Ok" : "Critical");
        return card;
    }

    private void OnUndo(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.Navigate(new UndoView(),
            "Undo previous changes",
            "Everything WinVitals has changed on this PC, and how to put it back.");

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Restart the computer now? Save any open work first.",
            "Restart", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        // /t 5 leaves a few seconds to cancel with "shutdown /a" if this was a misclick.
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false });
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        if (_scan is null)
        {
            MainWindow.Instance?.GoHome();
            return;
        }

        // Send them back to the results they came from rather than to the start: the
        // findings that were not repaired are still worth reading.
        MainWindow.Instance?.Navigate(new ResultsView(_scan, _playbook),
            "What I found",
            "These results are from before the repairs. Run the check again to confirm the changes took effect.");
    }
}
