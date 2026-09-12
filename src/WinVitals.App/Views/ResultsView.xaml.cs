using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinVitals.Core;
using WinVitals.Remediation;
using WinVitals.Report;

namespace WinVitals.App.Views;

public partial class ResultsView : UserControl
{
    private readonly ScanResult _scan;
    private readonly Playbook _playbook;

    /// <summary>Findings that have a repair available, paired with it.</summary>
    private readonly List<(Finding Finding, IFix Fix)> _repairable = new();

    public ResultsView(ScanResult scan, Playbook playbook)
    {
        InitializeComponent();
        _scan = scan;
        _playbook = playbook;

        BuildTiles();
        BuildFindings();
        BuildActionBar();
    }

    private void BuildTiles()
    {
        Tiles.Children.Add(Ui.Tile(_scan.Count(Severity.Critical), "Critical", "Critical"));
        Tiles.Children.Add(Ui.Tile(_scan.Count(Severity.Warning), "Need attention", "Warning"));
        Tiles.Children.Add(Ui.Tile(_scan.Count(Severity.Advisory), "Worth knowing", "Advisory"));
        Tiles.Children.Add(Ui.Tile(_scan.Count(Severity.Ok), "Healthy", "Ok"));
        Tiles.Children.Add(Ui.Tile(_scan.Count(Severity.Unknown), "Not checked", "Unknown"));
    }

    private void BuildFindings()
    {
        var ordered = _scan.AllFindings
            .Where(f => f.Severity != Severity.Ok)
            .OrderBy(f => (int)f.Severity)
            .ToList();

        if (ordered.Count == 0)
        {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("Everything checked out", "H2"));
            panel.Children.Add(Ui.Text(
                "WinVitals ran every check in this group and found nothing that needs your attention.",
                "Muted", new Thickness(0, 6, 0, 0)));
            FindingsHost.Children.Add(Ui.Card(panel));
            return;
        }

        foreach (var finding in ordered)
            FindingsHost.Children.Add(BuildCard(finding));

        // Healthy results are worth showing so people can see what was actually looked
        // at, but they belong out of the way at the bottom.
        var healthy = _scan.AllFindings.Where(f => f.Severity == Severity.Ok).ToList();
        if (healthy.Count == 0) return;

        var expander = Ui.Details($"Show the {healthy.Count} checks that came back healthy");
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var ok in healthy)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Ui.Text(ok.Title, "Body"));
            row.Children.Add(Ui.Text(ok.What, "Muted"));
            list.Children.Add(row);
        }
        expander.Content = list;
        FindingsHost.Children.Add(Ui.Card(expander, new Thickness(0, 4, 0, 0)));
    }

    private Border BuildCard(Finding finding)
    {
        var brushKey = Theme.BrushKey(finding.Severity);
        var body = new StackPanel();

        // Title row: severity chip, title, and the fix button if one exists.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pill = Ui.Pill(Theme.Word(finding.Severity), brushKey);
        pill.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(pill, 0);
        header.Children.Add(pill);

        var title = Ui.Text(finding.Title, "H2");
        title.FontSize = 16;
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var fix = FixCatalog.For(finding).FirstOrDefault();
        if (fix is not null)
        {
            _repairable.Add((finding, fix));

            var fixPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var button = Ui.Button("Fix this", "Primary", (_, _) => ApplyOne(finding, fix));
            button.MinWidth = 96;
            fixPanel.Children.Add(button);
            fixPanel.Children.Add(Ui.Text(RiskLabel(fix), "Label", new Thickness(0, 4, 0, 0)));
            ((TextBlock)fixPanel.Children[1]).TextAlignment = TextAlignment.Center;

            Grid.SetColumn(fixPanel, 2);
            header.Children.Add(fixPanel);
        }

        body.Children.Add(header);
        body.Children.Add(Ui.Text(finding.What, "Body", new Thickness(0, 10, 0, 0)));

        if (!string.IsNullOrWhiteSpace(finding.Why))
            body.Children.Add(Ui.Section("Why this matters", finding.Why!));

        if (!string.IsNullOrWhiteSpace(finding.Action))
            body.Children.Add(Ui.Section("What to do", finding.Action!));

        // Everything technical goes behind a disclosure so the card stays readable.
        var hasDetail = !string.IsNullOrWhiteSpace(finding.Evidence)
                        || !string.IsNullOrWhiteSpace(finding.Command)
                        || fix is not null;

        if (hasDetail)
        {
            var details = Ui.Details("Technical details");
            var inner = new StackPanel();

            if (fix is not null)
            {
                inner.Children.Add(Ui.Text("WHAT THE FIX WILL DO", "Label", new Thickness(0, 6, 0, 0)));
                inner.Children.Add(Ui.Text(fix.Explain, "Body", new Thickness(0, 3, 0, 0)));
                inner.Children.Add(Ui.CodeBox(SafePreview(fix, finding)));
            }
            else if (!string.IsNullOrWhiteSpace(finding.Command))
            {
                inner.Children.Add(Ui.Text("COMMAND YOU CAN RUN YOURSELF", "Label", new Thickness(0, 6, 0, 0)));
                inner.Children.Add(Ui.CodeBox(finding.Command!));
            }

            if (!string.IsNullOrWhiteSpace(finding.Evidence))
            {
                inner.Children.Add(Ui.Text("EVIDENCE", "Label", new Thickness(0, 12, 0, 0)));
                inner.Children.Add(Ui.CodeBox(finding.Evidence!));
            }

            if (!string.IsNullOrWhiteSpace(finding.VerifyAt))
            {
                var link = Ui.Button($"Open {finding.VerifyAt}", "Ghost", (_, _) => Open(finding.VerifyAt!));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                link.Margin = new Thickness(-8, 8, 0, 0);
                inner.Children.Add(link);
            }

            details.Content = inner;
            body.Children.Add(details);
        }

        var card = Ui.Card(body, new Thickness(0, 0, 0, 10));
        card.BorderThickness = new Thickness(4, 1, 1, 1);
        card.BorderBrush = Ui.Brush(brushKey);
        return card;
    }

    /// <summary>Preview can shell out, so a failure there must not take down the page.</summary>
    private static string SafePreview(IFix fix, Finding finding)
    {
        try { return fix.Preview(finding); }
        catch (Exception ex) { return $"(could not build a preview: {ex.Message})"; }
    }

    private static string RiskLabel(IFix fix) => fix.Risk switch
    {
        FixRisk.Safe => fix.Reversible ? "safe · undoable" : "safe · permanent",
        FixRisk.Moderate => fix.Reversible ? "needs care · undoable" : "needs care",
        _ => fix.Reversible ? "your call · undoable" : "your call · permanent",
    };

    private void BuildActionBar()
    {
        var bundle = SafeBundle();

        if (bundle.Count == 0)
        {
            var repairable = _repairable.Count;
            ActionHeadline.Text = repairable > 0 ? "Nothing safe to bundle" : "No automatic repairs available";
            ActionDetail.Text = repairable > 0
                ? $"{repairable} item(s) can be repaired, but each one needs your decision first. "
                  + "Use the Fix button on the individual result."
                : "Everything found here is either healthy or needs a human to decide what to do.";
            return;
        }

        if (!App.Elevated)
        {
            ActionHeadline.Text = "Repairs need administrator rights";
            ActionDetail.Text = "Restart WinVitals as administrator using the button above to enable repairs.";
            return;
        }

        FixAllButton.Visibility = Visibility.Visible;
        FixAllButton.Content = $"Fix what's safe ({bundle.Count})";
        ActionHeadline.Text = $"{bundle.Count} thing(s) can be fixed safely";
        ActionDetail.Text = "Each one is reversible, and WinVitals takes a verified restore point first. "
                            + "You will see exactly what will change before anything happens.";
    }

    /// <summary>
    /// The one-click batch: safe, reversible, and possible right now.
    ///
    /// Irreversible repairs are excluded even when they are low risk. A batch button is
    /// a promise that the user can change their mind afterwards.
    /// </summary>
    private List<(Finding Finding, IFix Fix)> SafeBundle() =>
        _repairable
            .Where(r => r.Fix.Risk == FixRisk.Safe && r.Fix.Reversible)
            .Where(r => !r.Fix.NeedsElevation || App.Elevated)
            .ToList();

    private void OnFixAll(object sender, RoutedEventArgs e)
    {
        var bundle = SafeBundle();
        if (bundle.Count == 0) return;

        var confirm = new ConfirmWindow(bundle.Select(b => (b.Fix, b.Finding)).ToList())
        {
            Owner = Window.GetWindow(this),
        };

        if (confirm.ShowDialog() != true) return;
        RunRepairs(bundle);
    }

    private void ApplyOne(Finding finding, IFix fix)
    {
        if (fix.NeedsElevation && !App.Elevated)
        {
            MessageBox.Show(Window.GetWindow(this),
                "This repair needs administrator rights. Use \"Restart as administrator\" at the top of the "
                + "window, then try again.",
                "WinVitals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = new ConfirmWindow(new List<(IFix, Finding)> { (fix, finding) })
        {
            Owner = Window.GetWindow(this),
        };

        if (confirm.ShowDialog() != true) return;
        RunRepairs(new List<(Finding, IFix)> { (finding, fix) });
    }

    private void RunRepairs(List<(Finding Finding, IFix Fix)> work)
    {
        MainWindow.Instance?.Navigate(new ApplyView(work, _scan, _playbook),
            "Applying repairs", "Taking a restore point first, then making the changes.");
    }

    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the report",
            Filter = "Web page (*.html)|*.html",
            FileName = $"WinVitals-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog() != true) return;

        var shareable = MessageBox.Show(Window.GetWindow(this),
            "Remove personal details from this copy?\n\n"
            + "Yes — replaces your username, PC name, serial number and network addresses with placeholders, "
            + "so the report is safe to email or post on a forum.\n\n"
            + "No — keeps the report exactly as it is, for your own records.",
            "Save report", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (shareable == MessageBoxResult.Cancel) return;

        try
        {
            var scan = shareable == MessageBoxResult.Yes ? Redact(_scan) : _scan;
            File.WriteAllText(dialog.FileName, HtmlReport.Render(scan, MachineTitle(shareable == MessageBoxResult.Yes)));
            Open(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Could not save the report: {ex.Message}",
                "WinVitals", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Rebuilds the scan with redaction applied.
    ///
    /// The scan is held unredacted in memory so the interface can show real paths and
    /// device names; redaction happens only on the copy that leaves the machine.
    /// </summary>
    private static ScanResult Redact(ScanResult scan)
    {
        var redactor = new Redactor(true);
        var copy = new ScanResult
        {
            ToolVersion = scan.ToolVersion,
            StartedAt = scan.StartedAt,
            Duration = scan.Duration,
            Elevated = scan.Elevated,
            Redacted = true,
        };

        foreach (var module in scan.Modules)
        {
            var clone = new ModuleResult { Name = module.Name, Blurb = module.Blurb, Duration = module.Duration };

            foreach (var f in module.Findings)
            {
                clone.Findings.Add(f with
                {
                    Title = redactor.Apply(f.Title)!,
                    What = redactor.Apply(f.What)!,
                    Why = redactor.Apply(f.Why),
                    Action = redactor.Apply(f.Action),
                    Command = redactor.Apply(f.Command),
                    UndoCommand = redactor.Apply(f.UndoCommand),
                    Evidence = redactor.Apply(f.Evidence),
                });
            }

            foreach (var fact in module.Facts)
                clone.Facts.Add(fact with { Value = redactor.Apply(fact.Value)!, Note = redactor.Apply(fact.Note) });

            clone.Error = redactor.Apply(module.Error);
            copy.Modules.Add(clone);
        }

        return copy;
    }

    private static string MachineTitle(bool redacted)
    {
        var cs = Wmi.First("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        var hardware = string.Join(" ", new[] { cs?.Str("Manufacturer") ?? "", cs?.Str("Model") ?? "" }
            .Where(s => s.Length > 0));
        var name = redacted ? "<machine>" : Environment.MachineName;
        return hardware.Length > 0 ? $"{hardware} ({name})" : name;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch { /* nothing useful to offer if the shell refuses */ }
    }
}
