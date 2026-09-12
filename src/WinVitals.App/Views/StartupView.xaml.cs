using System.Windows;
using System.Windows.Controls;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// Turn sign-in programs on and off.
///
/// Uses the same mechanism as Task Manager's Startup tab, which means a disabled entry
/// stays disabled and the program is not uninstalled. Every toggle is written to the
/// undo journal, so a user who switches off something they needed can find it again.
/// </summary>
public partial class StartupView : UserControl
{
    private UndoJournal? _journal;

    /// <summary>Set while the code is putting a checkbox back, to stop the handler re-entering.</summary>
    private bool _suppress;

    public StartupView()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        Host.Children.Clear();

        var items = StartupManager.List();

        var intro = new StackPanel();
        intro.Children.Add(Ui.Text($"{items.Count} program(s) start when you sign in", "H2"));
        intro.Children.Add(Ui.Text(
            "Turning one off here stops it launching automatically. It does not uninstall anything, and you "
            + "can still open the program normally. Anything you change can be put back from the Undo screen.",
            "Muted", new Thickness(0, 6, 0, 0)));

        if (!App.Elevated)
        {
            var note = Ui.Text(
                "Without administrator rights, only your own startup programs can be changed. Entries marked "
                + "\"All users\" will be refused.",
                "Body", new Thickness(0, 10, 0, 0));
            note.Foreground = Ui.Brush("Warning");
            intro.Children.Add(note);
        }

        Host.Children.Add(Ui.Card(intro, new Thickness(0, 0, 0, 14)));

        if (items.Count == 0)
        {
            Host.Children.Add(Ui.Card(Ui.Text("Nothing starts automatically at sign-in.", "Body")));
            return;
        }

        foreach (var item in items)
            Host.Children.Add(BuildRow(item));
    }

    private Border BuildRow(StartupItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(Ui.Text(item.Name, "H2"));
        text.Children.Add(Ui.Text(item.Origin, "Label", new Thickness(0, 3, 0, 0)));

        var command = Ui.Text(item.Command, "Muted", new Thickness(0, 6, 12, 0));
        command.FontSize = 12;
        command.TextTrimming = TextTrimming.CharacterEllipsis;
        command.MaxHeight = 34;
        text.Children.Add(command);

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Check"),
            IsChecked = item.Enabled,
            Content = item.Enabled ? "On" : "Off",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 60,
        };
        toggle.Checked += (_, _) => Toggle(item, true, toggle);
        toggle.Unchecked += (_, _) => Toggle(item, false, toggle);

        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        var card = Ui.Card(grid, new Thickness(0, 0, 0, 8));
        card.Padding = new Thickness(16, 12, 16, 12);
        return card;
    }

    private void Toggle(StartupItem item, bool enable, CheckBox toggle)
    {
        // Reverting the checkbox below raises Checked/Unchecked again. Without this
        // guard the refusal path would call itself back through the handler.
        if (_suppress) return;

        var (ok, message, undo) = StartupManager.SetEnabled(item, enable);

        if (!ok)
        {
            MessageBox.Show(Window.GetWindow(this), message, "WinVitals",
                MessageBoxButton.OK, MessageBoxImage.Information);

            // Put the control back to where the machine actually is, not where the
            // click implied it went.
            _suppress = true;
            try
            {
                toggle.IsChecked = item.Enabled;
                toggle.Content = item.Enabled ? "On" : "Off";
            }
            finally
            {
                _suppress = false;
            }
            return;
        }

        toggle.Content = enable ? "On" : "Off";

        if (undo is null) return;
        _journal ??= new UndoJournal(AppInfo.Version);
        _journal.Record($"startup.{item.Name}", $"{(enable ? "Enabled" : "Disabled")} \"{item.Name}\" at sign-in",
            new List<UndoStep> { undo });
    }
}
