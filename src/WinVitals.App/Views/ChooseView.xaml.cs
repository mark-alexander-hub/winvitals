using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;

namespace WinVitals.App.Views;

/// <summary>The symptom chooser: the Diagnose page.</summary>
public partial class ChooseView : UserControl
{
    /// <summary>A playbook plus the icon glyph shown beside it.</summary>
    public sealed record Entry(string Id, string Title, string Subtitle, string Duration, string Glyph);

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["full"] = "",     // Diagnostic
        ["slow"] = "",     // Speed
        ["sleep"] = "",    // Moon / quiet hours
        ["battery"] = "",  // Battery
        ["network"] = "",  // Wi-Fi
        ["crash"] = "",    // Warning
        ["space"] = "",    // Hard drive
        ["security"] = "", // Lock
    };

    public ChooseView()
    {
        InitializeComponent();
        PlaybookList.ItemsSource = Playbooks.All
            .Select(p => new Entry(p.Id, p.Title, p.Subtitle, p.Duration,
                Glyphs.TryGetValue(p.Id, out var g) ? g : ""))
            .ToList();
    }

    private void OnPlaybookChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var playbook = Playbooks.ById(id);
        if (playbook is null) return;

        MainWindow.Instance?.Navigate(new ScanView(playbook), "Checking your PC", playbook.Title);
    }
}
