using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinVitals.App.Views;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    /// <summary>Set while code selects a nav item, so the Checked handler does not navigate twice.</summary>
    private bool _selecting;

    public MainWindow(string? startWithCheck = null)
    {
        InitializeComponent();
        Instance = this;

        VersionText.Text = $"v{AppInfo.Version}";
        if (!App.Elevated)
        {
            AdminText.Text = "Standard user — repairs off";
            AdminDot.Fill = Ui.Brush("Warning");
        }

        var playbook = startWithCheck is null ? null : Playbooks.ById(startWithCheck);
        if (playbook is not null)
        {
            Select(NavDiagnose);
            Navigate(new ScanView(playbook), "Checking your PC", playbook.Title);
        }
        else
        {
            NavHome.IsChecked = true;
        }
    }

    /// <summary>Swaps the page and sets the header to match it.</summary>
    public void Navigate(UserControl view, string title, string subtitle, bool showBack = true)
    {
        HeaderTitle.Text = title;
        HeaderSubtitle.Text = subtitle;
        PageHost.Content = view;
        Log.Info($"Navigate: {view.GetType().Name} — {title}");
    }

    public void GoHome() => ShowPage("home");

    /// <summary>Selects a rail entry by id and shows its page.</summary>
    public void ShowPage(string id)
    {
        var item = id.ToLowerInvariant() switch
        {
            "home" => NavHome,
            "diagnose" => NavDiagnose,
            "cleanup" => NavCleanup,
            "speedup" => NavSpeedup,
            "repair" => NavRepair,
            "undo" => NavUndo,
            _ => NavHome,
        };

        // Re-selecting the current item would not fire Checked, so navigate directly.
        if (item.IsChecked == true) Open(item);
        else item.IsChecked = true;
    }

    private void Select(RadioButton item)
    {
        _selecting = true;
        try { item.IsChecked = true; }
        finally { _selecting = false; }
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (_selecting || sender is not RadioButton item) return;
        Open(item);
    }

    private void Open(RadioButton item)
    {
        try
        {
            if (item == NavHome)
                Navigate(new HomeView(), "Home", "How this PC is doing right now, and where to start.");
            else if (item == NavDiagnose)
                Navigate(new ChooseView(), "Diagnose",
                    "Pick what is wrong in your own words. WinVitals runs the right checks and explains what it finds.");
            else if (item == NavCleanup)
                Navigate(new ToolsView(
                        "What is using your disk, and the safe ways to get space back.",
                        showSpace: true, FixCategory.Storage),
                    "Clean up", "Reclaim disk space without deleting anything you saved.");
            else if (item == NavSpeedup)
                Navigate(new StartupView(), "Speed up",
                    "Programs that launch at sign-in are the usual reason an older PC feels slow. Turn off what you do not need.");
            else if (item == NavRepair)
                Navigate(new ToolsView(
                        "The standard Windows repairs, each explained. Nothing runs until you confirm it.",
                        showSpace: false, FixCategory.Repair, FixCategory.Network, FixCategory.Security, FixCategory.Power),
                    "Repair", "Fix Windows itself: system files, updates, networking, security settings.");
            else if (item == NavUndo)
                Navigate(new UndoView(), "Undo changes",
                    "Everything WinVitals has changed on this PC, and a button to put each one back.");
        }
        catch (Exception ex)
        {
            // A page that fails to build must not take the window down; show why instead.
            Log.Error($"Failed to open page {item.Name}", ex);
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("This page could not be opened", "H2"));
            panel.Children.Add(Ui.Text(ex.Message, "Body", new Thickness(0, 8, 0, 0)));
            panel.Children.Add(Ui.Text($"Details are in {Log.Path_}", "Muted", new Thickness(0, 8, 0, 0)));
            var card = Ui.Card(panel, new Thickness(32, 0, 32, 0));
            PageHost.Content = card;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Log.Directory, UseShellExecute = true }); }
        catch { /* nothing useful to do if the shell refuses */ }
    }
}
