using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// The dashboard: a health score from the last check-up, a few live numbers that
/// are cheap to read, the three things a person can do next, and the weekly
/// check-up switch.
/// </summary>
public partial class HomeView : UserControl
{
    private UpdateCheck.Release? _update;

    /// <summary>Set while code changes the weekly checkbox, so the handler does not act on it.</summary>
    private bool _settingToggle;

    public HomeView()
    {
        InitializeComponent();
        BuildHealth();
        BuildRecent();
        BuildWeekly();
        Loaded += (_, _) =>
        {
            _ = LoadStatsAsync();
            _ = CheckForUpdateAsync();
        };
    }

    // ---------------------------------------------------------------- health

    private void BuildHealth()
    {
        var scan = Session.LastScan;
        if (scan is null)
        {
            RingHost.Content = Ui.Ring(null, 132, 11);

            // No scan this session, but a scheduled one may have run recently.
            var last = ScheduledSummary.Load();
            if (last is not null && (DateTimeOffset.Now - last.When).TotalDays <= 8)
            {
                RingHost.Content = Ui.Ring(last.Score, 132, 11);
                ScoreWord.Text = Session.ScoreWord(last.Score);
                ScoreDetail.Text = $"From the automatic check-up on {last.When:ddd d MMM 'at' HH:mm}.";
            }
            return;
        }

        var score = Session.Score(scan);
        RingHost.Content = Ui.Ring(score, 132, 11);
        ScoreWord.Text = Session.ScoreWord(score);

        var parts = new List<string>();
        if (scan.Count(Severity.Critical) > 0) parts.Add($"{scan.Count(Severity.Critical)} critical");
        if (scan.Count(Severity.Warning) > 0) parts.Add($"{scan.Count(Severity.Warning)} need attention");
        if (scan.Count(Severity.Advisory) > 0) parts.Add($"{scan.Count(Severity.Advisory)} worth knowing");
        if (parts.Count == 0) parts.Add("nothing needs attention");

        ScoreDetail.Text = $"{string.Join(", ", parts)} — checked at {scan.StartedAt:HH:mm}"
                           + (Session.LastPlaybook is { } pb && pb.Modules.Count > 0 ? $" ({pb.Title})" : "");
        ViewResultsButton.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- recent

    private void BuildRecent()
    {
        try
        {
            var latest = UndoJournal.LoadAll().FirstOrDefault();
            if (latest.Session is null || latest.Session.Entries.Count == 0) return;

            var s = latest.Session;
            var undone = s.Entries.Count(e => e.Undone);
            RecentText.Text = $"{s.Entries.Count} change(s) on {s.Started:ddd d MMM 'at' HH:mm}"
                              + (undone > 0 ? $", {undone} already undone" : "")
                              + $": {string.Join("; ", s.Entries.Take(3).Select(e => e.Title))}"
                              + (s.Entries.Count > 3 ? "…" : ".");
            RecentCard.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the undo journal for the dashboard: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- weekly

    private void BuildWeekly()
    {
        _settingToggle = true;
        try
        {
            WeeklyToggle.IsChecked = Scheduler.IsScheduled();
            WeeklyToggle.Content = WeeklyToggle.IsChecked == true ? "On" : "Off";
        }
        finally { _settingToggle = false; }

        var last = ScheduledSummary.Load();
        if (last is null) return;

        WeeklyLast.Text = $"Last automatic check-up: {last.When:ddd d MMM 'at' HH:mm} — score {last.Score}, "
                          + $"{last.Critical} critical, {last.Warning} need attention, {last.Advisory} worth knowing.";
        WeeklyLast.Visibility = Visibility.Visible;
        WeeklyReport.Visibility = File.Exists(last.ReportPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWeeklyChanged(object sender, RoutedEventArgs e)
    {
        if (_settingToggle) return;

        var turnOn = WeeklyToggle.IsChecked == true;
        var (ok, message) = turnOn ? Scheduler.Enable() : Scheduler.Disable();

        if (!ok)
        {
            MessageBox.Show(Window.GetWindow(this), message, "WinVitals", MessageBoxButton.OK, MessageBoxImage.Warning);
            _settingToggle = true;
            try { WeeklyToggle.IsChecked = !turnOn; }
            finally { _settingToggle = false; }
            return;
        }

        WeeklyToggle.Content = turnOn ? "On" : "Off";
        WeeklyText.Text = turnOn
            ? message + " Keep WinVitals.exe where it is now, or the task will not find it."
            : "Let WinVitals check this PC every Sunday at 10:00 and keep the report here. It only reads; nothing is changed without you.";
    }

    private void OnOpenWeeklyReport(object sender, RoutedEventArgs e)
    {
        var last = ScheduledSummary.Load();
        if (last is not null && File.Exists(last.ReportPath)) Open(last.ReportPath);
    }

    // ---------------------------------------------------------------- update

    private async Task CheckForUpdateAsync()
    {
        _update = await UpdateCheck.NewerAsync();
        if (_update is null) return;

        UpdateText.Text = $"WinVitals {_update.Tag} is available — you have {AppInfo.Version}.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnGetUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is not null) Open(_update.Url);
    }

    // ----------------------------------------------------------------- stats

    /// <summary>
    /// Four numbers that are cheap to read and mean something at a glance. Each is
    /// independent: one failing must not blank the others.
    /// </summary>
    private async Task LoadStatsAsync()
    {
        var tiles = new (string Label, Func<(string Value, string Note)> Read)[]
        {
            ("Free space", FreeSpace),
            ("Memory in use", MemoryUse),
            ("Running for", Uptime),
            ("Battery", Battery),
        };

        foreach (var (label, _) in tiles)
            Stats.Children.Add(Ui.Stat(label, "…", ""));

        for (var i = 0; i < tiles.Length; i++)
        {
            var read = tiles[i].Read;
            var result = await Task.Run(() =>
            {
                try { return read(); }
                catch (Exception ex)
                {
                    Log.Warn($"Dashboard stat failed: {ex.Message}");
                    return ("—", "could not read");
                }
            });

            var index = i;
            Stats.Children.RemoveAt(index);
            Stats.Children.Insert(index, Ui.Stat(tiles[index].Label, result.Item1, result.Item2));
        }
    }

    private static (string, string) FreeSpace()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drive = new DriveInfo(root);
        var pct = drive.AvailableFreeSpace * 100.0 / Math.Max(drive.TotalSize, 1);
        return (CollectorExtensions.Bytes(drive.AvailableFreeSpace), $"{pct:0}% of {root.TrimEnd('\\')} free");
    }

    private static (string, string) MemoryUse()
    {
        var os = Wmi.First("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
        if (os is null) return ("—", "not available");
        var total = os.Num("TotalVisibleMemorySize");
        var free = os.Num("FreePhysicalMemory");
        var pct = (total - free) * 100.0 / Math.Max(total, 1);
        return ($"{pct:0}%", $"{CollectorExtensions.Bytes(free * 1024)} free of {CollectorExtensions.Bytes(total * 1024)}");
    }

    private static (string, string) Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var note = up.TotalDays >= 14 ? "a restart would help" : "since last restart";
        return (CollectorExtensions.Duration(up), note);
    }

    private static (string, string) Battery()
    {
        if (Wmi.First("SELECT Name FROM Win32_Battery") is null) return ("None", "desktop, or battery removed");

        var design = Wmi.First("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi")?.Num("DesignedCapacity") ?? 0;
        var full = Wmi.First("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi")?.Num("FullChargedCapacity") ?? 0;
        if (design <= 0 || full <= 0) return ("Present", "wear not reported");

        var health = full * 100.0 / design;
        return ($"{health:0}%", "of original capacity");
    }

    // ------------------------------------------------------------ navigation

    private void OnFullCheck(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.Navigate(new ScanView(Playbooks.Full), "Checking your PC", Playbooks.Full.Title);

    private void OnDiagnose(object sender, RoutedEventArgs e) => MainWindow.Instance?.ShowPage("diagnose");
    private void OnCleanup(object sender, RoutedEventArgs e) => MainWindow.Instance?.ShowPage("cleanup");
    private void OnSpeedup(object sender, RoutedEventArgs e) => MainWindow.Instance?.ShowPage("speedup");
    private void OnUndo(object sender, RoutedEventArgs e) => MainWindow.Instance?.ShowPage("undo");

    private void OnViewResults(object sender, RoutedEventArgs e)
    {
        if (Session.LastScan is null || Session.LastPlaybook is null) return;
        MainWindow.Instance?.Navigate(new ResultsView(Session.LastScan, Session.LastPlaybook),
            "What I found", $"From the check-up at {Session.LastScan.StartedAt:HH:mm}.");
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
    }
}
