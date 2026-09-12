using System.Windows;
using WinVitals.Core;
using WinVitals.Remediation;

namespace WinVitals.App.Views;

/// <summary>The one path by which a standalone tool gets run, whichever page offered it.</summary>
internal static class Repairs
{
    public static void RunTool(IFix fix, DependencyObject from)
    {
        var owner = Window.GetWindow(from);

        if (fix.NeedsElevation && !App.Elevated)
        {
            MessageBox.Show(owner,
                "This tool needs administrator rights. Close WinVitals and run it as administrator.",
                "WinVitals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Tools are not attached to a finding, so they get a stand-in carrying the same
        // identity. Standalone fixes never read the finding's evidence.
        var placeholder = new Finding
        {
            Id = fix.FindingId,
            Module = "Tools",
            Severity = Severity.Advisory,
            Title = fix.Title,
            What = fix.Explain,
        };

        var confirm = new ConfirmWindow(new List<(IFix, Finding)> { (fix, placeholder) }) { Owner = owner };
        if (confirm.ShowDialog() != true) return;

        MainWindow.Instance?.Navigate(
            new ApplyView(new List<(Finding, IFix)> { (placeholder, fix) }, null, Playbooks.Full),
            "Running repair", fix.Title);
    }
}
