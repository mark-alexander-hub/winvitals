using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// Speed up: boot time with a before-and-after, the startup program list, and the
/// tools that make an older machine feel quicker.
/// </summary>
public partial class SpeedupView : UserControl
{
    public SpeedupView()
    {
        InitializeComponent();

        foreach (var fix in FixCatalog.ToolsIn(FixCategory.Performance))
            Tools.Children.Add(Ui.ToolCard(fix, f => Repairs.RunTool(f, this)));

        Loaded += (_, _) => _ = LoadBootAsync();
    }

    private async Task LoadBootAsync()
    {
        var boots = await Task.Run(() =>
        {
            try { return BootHistory.Read(10); }
            catch (Exception ex)
            {
                Log.Warn($"Boot history failed: {ex.Message}");
                return Array.Empty<BootRecord>() as IReadOnlyList<BootRecord>;
            }
        });

        if (boots.Count == 0)
        {
            BootBig.Text = "—";
            BootHeadline.Text = "No boot timing recorded yet";
            BootDetail.Text = App.Elevated
                ? "Windows logs a boot time only after a real restart, not after waking from sleep. Restart, then come back here."
                : "Reading boot times needs administrator rights.";
            return;
        }

        var last = boots[0];
        BootBig.Text = $"{last.TotalMs / 1000.0:0} s";
        BootWhen.Text = last.When.ToString("ddd d MMM, HH:mm");

        var seconds = last.TotalMs / 1000.0;
        BootHeadline.Text = seconds >= 120 ? "That is slow — startup programs are the usual reason"
            : seconds >= 60 ? "Room to improve"
            : "Already quick";

        BootDetail.Text = last.PostBootMs > 0
            ? $"About {last.PostBootMs / 1000.0:0} seconds of that was startup programs loading after the desktop "
              + "appeared. That is the part the list below changes."
            : "Turn off startup programs you do not need below, then restart to measure the difference.";

        var snapshot = SpeedupSnapshot.Load();
        if (snapshot is null) return;

        // The first boot that happened after the user's changes is the "after".
        var after = boots.LastOrDefault(b => b.When > snapshot.TakenAt.LocalDateTime);
        Comparison.Visibility = Visibility.Visible;
        ResetCompare.Visibility = Visibility.Visible;

        if (after is null)
        {
            Comparison.Text = $"Before your changes this PC booted in {snapshot.BootMs / 1000.0:0} s. "
                              + "Restart (not shut down) to measure the after.";
            return;
        }

        var saved = (snapshot.BootMs - after.TotalMs) / 1000.0;
        Comparison.Text = saved >= 1
            ? $"Before: {snapshot.BootMs / 1000.0:0} s  →  After: {after.TotalMs / 1000.0:0} s.  "
              + $"You saved {saved:0} seconds on every start."
            : $"Before: {snapshot.BootMs / 1000.0:0} s  →  After: {after.TotalMs / 1000.0:0} s.  "
              + "No measurable change yet — boot times vary by a few seconds run to run.";
        Comparison.Foreground = Ui.Brush(saved >= 1 ? "Ok" : "Muted");
    }

    private void OnResetComparison(object sender, RoutedEventArgs e)
    {
        SpeedupSnapshot.Clear();
        Comparison.Visibility = Visibility.Collapsed;
        ResetCompare.Visibility = Visibility.Collapsed;
    }
}
