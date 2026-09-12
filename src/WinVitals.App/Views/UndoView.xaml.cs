using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// The complete history of what WinVitals has changed on this machine, and a button
/// to reverse each of it.
///
/// This screen is the reason the tool can be trusted to change anything at all.
/// </summary>
public partial class UndoView : UserControl
{
    public UndoView()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        Host.Children.Clear();

        var sessions = UndoJournal.LoadAll();

        if (sessions.Count == 0)
        {
            var empty = new StackPanel();
            empty.Children.Add(Ui.Text("WinVitals has not changed anything on this PC", "H2"));
            empty.Children.Add(Ui.Text(
                "Once you apply a repair it will be listed here, with a button to put it back.",
                "Muted", new Thickness(0, 6, 0, 0)));
            Host.Children.Add(Ui.Card(empty));
            return;
        }

        var openFolder = Ui.Button("Open the folder holding these records", "Ghost",
            (_, _) => Open(UndoJournal.Directory));
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.Margin = new Thickness(-8, 0, 0, 10);
        Host.Children.Add(openFolder);

        foreach (var (path, session) in sessions)
            Host.Children.Add(BuildSession(path, session));
    }

    private Border BuildSession(string path, UndoSession session)
    {
        var panel = new StackPanel();

        panel.Children.Add(Ui.Text(session.Started.ToString("dddd d MMMM yyyy, HH:mm"), "H2"));
        panel.Children.Add(Ui.Text(
            $"WinVitals {session.ToolVersion} · restore point: {session.RestorePoint ?? "not recorded"}",
            "Label", new Thickness(0, 4, 0, 0)));

        if (session.Entries.Count == 0)
        {
            panel.Children.Add(Ui.Text("Nothing was changed in this session.", "Muted",
                new Thickness(0, 10, 0, 0)));
            return Ui.Card(panel, new Thickness(0, 0, 0, 10));
        }

        foreach (var entry in session.Entries)
            panel.Children.Add(BuildEntry(path, session, entry));

        return Ui.Card(panel, new Thickness(0, 0, 0, 10));
    }

    private Border BuildEntry(string path, UndoSession session, UndoEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(Ui.Text(entry.Title, "Body"));
        text.Children.Add(Ui.Text(
            string.Join("  ·  ", entry.Steps.Select(s => s.Describe)),
            "Muted", new Thickness(0, 4, 12, 0)));

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (entry.Undone)
        {
            var done = Ui.Pill("Undone", "Ok");
            done.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(done, 1);
            grid.Children.Add(done);
        }
        else
        {
            var button = Ui.Button("Undo", "Secondary", (_, _) => Undo(path, session, entry));
            button.MinWidth = 84;
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return new Border
        {
            Background = Ui.Brush("CodeBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 10, 0, 0),
            Child = grid,
        };
    }

    private void Undo(string path, UndoSession session, UndoEntry entry)
    {
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Put this back?\n\n{entry.Title}\n\n"
            + string.Join("\n", entry.Steps.Select(s => "• " + s.Describe)),
            "Undo change", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (confirm != MessageBoxResult.Yes) return;

        var (ok, messages) = UndoRunner.Undo(entry);
        UndoJournal.SaveTo(path, session);

        MessageBox.Show(Window.GetWindow(this),
            (ok ? "Put back successfully.\n\n" : "Some steps could not be reversed.\n\n")
            + string.Join("\n", messages),
            "Undo", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Build();
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch { /* nothing useful to offer if the shell refuses */ }
    }
}
