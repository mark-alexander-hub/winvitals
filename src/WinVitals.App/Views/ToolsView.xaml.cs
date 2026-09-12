using System.Windows;
using System.Windows.Controls;
using WinVitals.Collectors;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// A category of repairs, reachable directly rather than because a check pointed at
/// them. Backs both the Clean up page (storage tools, with a live look at what is
/// using the disk) and the Repair page (everything else).
/// </summary>
public partial class ToolsView : UserControl
{
    private readonly bool _showSpace;

    public ToolsView(string intro, bool showSpace, params FixCategory[] categories)
    {
        InitializeComponent();
        _showSpace = showSpace;

        Host.Children.Add(Ui.Card(Ui.Text(intro, "Body"), new Thickness(0, 0, 0, 14)));

        if (showSpace)
        {
            Host.Children.Add(SpaceCardPlaceholder());
            Loaded += (_, _) => _ = LoadSpaceAsync();
        }

        var tools = categories.Length == 0 ? FixCatalog.Tools : FixCatalog.ToolsIn(categories).ToList();
        foreach (var fix in tools)
            Host.Children.Add(BuildCard(fix));
    }

    // ------------------------------------------------------------ disk space

    private Border? _spaceCard;

    private Border SpaceCardPlaceholder()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("WHAT IS USING THE DISK", "Label"));
        panel.Children.Add(Ui.Text("Measuring…", "Muted", new Thickness(0, 6, 0, 0)));
        _spaceCard = Ui.Card(panel, new Thickness(0, 0, 0, 14));
        return _spaceCard;
    }

    /// <summary>Runs just the storage collector and shows its facts as a table.</summary>
    private async Task LoadSpaceAsync()
    {
        var module = await Task.Run(() =>
        {
            var result = new ModuleResult { Name = "Storage", Blurb = "" };
            try
            {
                new StorageCollector().Collect(new ScanContext
                {
                    Elevated = App.Elevated,
                    Redactor = new Redactor(false),
                }, result);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log.Error("Storage measurement failed", ex);
            }
            return result;
        });

        if (_spaceCard is null) return;

        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("WHAT IS USING THE DISK", "Label"));

        if (module.Error is not null)
        {
            panel.Children.Add(Ui.Text($"Could not measure: {module.Error}", "Muted", new Thickness(0, 6, 0, 0)));
        }
        else
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var row = 0;
            foreach (var fact in module.Facts)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = Ui.Text(fact.Label, "Muted", new Thickness(0, 3, 16, 3));
                Grid.SetRow(label, row); Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                var value = Ui.Text(fact.Value + (fact.Note is null ? "" : $"  ·  {fact.Note}"), "Body",
                    new Thickness(0, 3, 0, 3));
                Grid.SetRow(value, row); Grid.SetColumn(value, 1);
                grid.Children.Add(value);
                row++;
            }
            panel.Children.Add(grid);

            // The storage findings that matter for cleanup, in brief.
            foreach (var f in module.Findings.Where(f => f.Severity is Severity.Critical or Severity.Warning or Severity.Advisory))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var pill = Ui.Pill(Theme.Word(f.Severity), Theme.BrushKey(f.Severity));
                pill.Margin = new Thickness(0, 0, 10, 0);
                line.Children.Add(pill);
                var text = Ui.Text(f.Title, "Body");
                text.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(text);
                panel.Children.Add(line);
            }
        }

        _spaceCard.Child = panel;
    }

    // ------------------------------------------------------------------ tools

    private Border BuildCard(IFix fix)
    {
        var panel = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(Ui.Text(fix.Title, "H2"));

        var risk = fix.Risk switch
        {
            FixRisk.Safe => "Safe to run at any time",
            FixRisk.Moderate => "Takes a while, or needs a restart",
            _ => "Changes something you may rely on",
        };
        var undo = fix.Reversible ? "can be undone"
            : fix.NothingToUndo ? "nothing to undo"
            : "cannot be undone";
        var meta = $"{risk} · {undo}";
        titles.Children.Add(Ui.Text(meta, "Label", new Thickness(0, 4, 0, 0)));

        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        var run = Ui.Button("Run", "Secondary", (_, _) => Run(fix));
        run.MinWidth = 84;
        run.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(run, 1);
        header.Children.Add(run);

        panel.Children.Add(header);
        panel.Children.Add(Ui.Text(fix.Explain, "Body", new Thickness(0, 10, 0, 0)));

        var card = Ui.Card(panel, new Thickness(0, 0, 0, 10));
        card.BorderThickness = new Thickness(4, 1, 1, 1);
        card.BorderBrush = Ui.Brush(fix.Risk == FixRisk.Safe ? "Ok" : "Warning");
        return card;
    }

    private void Run(IFix fix)
    {
        if (fix.NeedsElevation && !App.Elevated)
        {
            MessageBox.Show(Window.GetWindow(this),
                "This tool needs administrator rights. Close WinVitals and run it as administrator.",
                "WinVitals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Tools are not attached to a finding, so they get a stand-in carrying the same
        // identity. Standalone fixes never read the finding's evidence.
        var placeholder = new Finding
        {
            Id = fix.FindingId,
            Module = "Repair tools",
            Severity = Severity.Advisory,
            Title = fix.Title,
            What = fix.Explain,
        };

        var confirm = new ConfirmWindow(new List<(IFix, Finding)> { (fix, placeholder) })
        {
            Owner = Window.GetWindow(this),
        };

        if (confirm.ShowDialog() != true) return;

        MainWindow.Instance?.Navigate(
            new ApplyView(new List<(Finding, IFix)> { (placeholder, fix) }, null, Playbooks.Full),
            "Running repair", fix.Title);
    }
}
