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
    public ToolsView(string intro, bool showSpace, params FixCategory[] categories)
    {
        InitializeComponent();

        Host.Children.Add(Ui.Card(Ui.Text(intro, "Body"), new Thickness(0, 0, 0, 14)));

        if (showSpace)
        {
            Host.Children.Add(SpaceCardPlaceholder());
            Loaded += (_, _) => _ = LoadSpaceAsync();
        }

        var tools = categories.Length == 0 ? FixCatalog.Tools : FixCatalog.ToolsIn(categories).ToList();
        foreach (var fix in tools)
            Host.Children.Add(Ui.ToolCard(fix, f => Repairs.RunTool(f, this)));
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
}
