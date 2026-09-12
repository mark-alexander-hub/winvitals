using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinVitals.App.Views;

namespace WinVitals.App;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    public MainWindow(string? startWithCheck = null)
    {
        InitializeComponent();
        Instance = this;

        if (!App.Elevated) AdminBanner.Visibility = Visibility.Visible;

        var playbook = startWithCheck is null ? null : Core.Playbooks.ById(startWithCheck);
        if (playbook is not null)
            Navigate(new ScanView(playbook), "Checking your PC", playbook.Title);
        else
            GoHome();
    }

    /// <summary>Swaps the page and sets the header to match it.</summary>
    public void Navigate(UserControl view, string title, string subtitle, bool showBack = true)
    {
        HeaderTitle.Text = title;
        HeaderSubtitle.Text = subtitle;
        BackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Content = view;
    }

    public void GoHome()
    {
        Navigate(new ChooseView(),
            "What can I help with?",
            "Pick what is wrong and WinVitals will check it, explain what it finds, and offer to fix it.",
            showBack: false);
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoHome();

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch
        {
            MessageBox.Show(this,
                "Windows did not allow the restart. You can right-click WinVitals and choose "
                + "\"Run as administrator\" instead.",
                "WinVitals", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
