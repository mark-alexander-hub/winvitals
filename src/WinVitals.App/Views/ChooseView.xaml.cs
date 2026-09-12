using System.Windows;
using System.Windows.Controls;
using WinVitals.Core;

namespace WinVitals.App.Views;

public partial class ChooseView : UserControl
{
    public ChooseView()
    {
        InitializeComponent();
        PlaybookList.ItemsSource = Playbooks.All;
    }

    private void OnPlaybookChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var playbook = Playbooks.ById(id);
        if (playbook is null) return;

        MainWindow.Instance?.Navigate(new ScanView(playbook),
            "Checking your PC", playbook.Title);
    }

    private void OnTools(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.Navigate(new ToolsView(),
            "Repair tools",
            "Standard Windows repairs, with an explanation of what each one actually does.");

    private void OnStartup(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.Navigate(new StartupView(),
            "Startup programs",
            "Programs that launch when you sign in. Turning one off here does not uninstall it.");

    private void OnUndo(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.Navigate(new UndoView(),
            "Undo previous changes",
            "Everything WinVitals has changed on this PC, and how to put it back.");
}
