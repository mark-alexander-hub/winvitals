using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// The standard Windows repairs, reachable directly rather than because a check
/// pointed at them — for when someone already knows what they want to try.
/// </summary>
public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();

        foreach (var fix in FixCatalog.Tools)
            Host.Children.Add(BuildCard(fix));
    }

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
        var meta = fix.Reversible ? $"{risk} · can be undone" : $"{risk} · cannot be undone";
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
                "This tool needs administrator rights. Use \"Restart as administrator\" at the top of the "
                + "window, then try again.",
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
