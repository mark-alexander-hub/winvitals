using System.Windows;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>
/// Shows precisely what a repair will do before it does it, including the commands
/// and whether it can be undone.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(List<(IFix Fix, Finding Finding)> work)
    {
        InitializeComponent();

        Headline.Text = work.Count == 1
            ? work[0].Fix.Title
            : $"Review {work.Count} changes";

        ApplyButton.Content = work.Count == 1 ? "Apply this fix" : $"Apply all {work.Count}";

        foreach (var (fix, finding) in work)
            Host.Children.Add(BuildEntry(fix, finding, work.Count > 1));
    }

    private static UIElement BuildEntry(IFix fix, Finding finding, bool numbered)
    {
        var panel = new System.Windows.Controls.StackPanel();

        panel.Children.Add(Ui.Text(fix.Title, "H2"));
        panel.Children.Add(Ui.Text(fix.Explain, "Body", new Thickness(0, 6, 0, 0)));

        string preview;
        try { preview = fix.Preview(finding); }
        catch (Exception ex) { preview = $"(could not build a preview: {ex.Message})"; }

        panel.Children.Add(Ui.Text("EXACTLY WHAT RUNS", "Label", new Thickness(0, 12, 0, 0)));
        panel.Children.Add(Ui.CodeBox(preview));

        var reversal = fix.Reversible
            ? "This can be undone from the \"Undo previous changes\" screen at any time."
            : "This CANNOT be undone. Make sure you are happy before continuing.";

        var note = Ui.Text(reversal, "Body", new Thickness(0, 10, 0, 0));
        note.Foreground = Ui.Brush(fix.Reversible ? "Muted" : "Warning");
        panel.Children.Add(note);

        if (fix.NeedsRestart)
        {
            var restart = Ui.Text("A restart is needed before this takes effect.", "Body",
                new Thickness(0, 4, 0, 0));
            restart.Foreground = Ui.Brush("Warning");
            panel.Children.Add(restart);
        }

        var card = Ui.Card(panel, new Thickness(0, 0, 0, 10));
        if (!numbered) card.Margin = new Thickness(0);
        return card;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
