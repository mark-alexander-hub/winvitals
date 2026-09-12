using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinVitals.App;

/// <summary>
/// Installs the colour palette as application resources, following whatever the user
/// has Windows set to.
///
/// Defined in code rather than as two XAML dictionaries so there is exactly one list
/// of colour names, and a palette entry cannot exist in the light theme but be missing
/// from the dark one.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Bg"] = ("#F4F6F8", "#15181C"),
        ["Card"] = ("#FFFFFF", "#1D2126"),
        ["CardHover"] = ("#F7F9FB", "#242930"),
        ["Ink"] = ("#14171A", "#E9ECEF"),
        ["Muted"] = ("#5B6570", "#98A2AD"),
        ["Line"] = ("#E1E6EB", "#2D333A"),
        ["CodeBg"] = ("#F0F2F5", "#23282E"),

        ["Accent"] = ("#1B5FA8", "#7FB6F0"),
        ["AccentInk"] = ("#FFFFFF", "#0E1216"),
        ["AccentSoft"] = ("#E8F1FB", "#1B2836"),

        ["Critical"] = ("#B3261E", "#FF8F84"),
        ["CriticalSoft"] = ("#FCEBEA", "#34201F"),
        ["Warning"] = ("#8A4B00", "#F0B860"),
        ["WarningSoft"] = ("#FDF2E3", "#332918"),
        ["Advisory"] = ("#1B5FA8", "#7FB6F0"),
        ["AdvisorySoft"] = ("#E8F1FB", "#1B2836"),
        ["Ok"] = ("#1B6B40", "#79D79F"),
        ["OkSoft"] = ("#E8F5ED", "#182B20"),
        ["Unknown"] = ("#6B7280", "#A6AEB8"),
        ["UnknownSoft"] = ("#F0F1F3", "#24282D"),
    };

    public static void Apply(Application app)
    {
        IsDark = SystemPrefersDark();

        foreach (var (name, colours) in Palette)
        {
            var colour = (Color)ColorConverter.ConvertFromString(IsDark ? colours.Dark : colours.Light)!;
            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            // The "Brush" suffix keeps the palette out of the same namespace as the
            // control styles. Without it a palette entry called Muted or Card silently
            // shadows the TextBlock or Border style of the same name, and WPF then
            // throws when it is handed a brush where a Style was expected.
            app.Resources[name + "Brush"] = brush;
            app.Resources[name + "Color"] = colour;
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme is 0 for dark. Absent means light.
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not null && Convert.ToInt32(value) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Brush for a severity, by name, so views do not repeat the mapping.</summary>
    public static string BrushKey(Core.Severity severity) => severity switch
    {
        Core.Severity.Critical => "Critical",
        Core.Severity.Warning => "Warning",
        Core.Severity.Advisory => "Advisory",
        Core.Severity.Ok => "Ok",
        _ => "Unknown",
    };

    public static string Word(Core.Severity severity) => severity switch
    {
        Core.Severity.Critical => "Critical",
        Core.Severity.Warning => "Needs attention",
        Core.Severity.Advisory => "Worth knowing",
        Core.Severity.Ok => "Healthy",
        _ => "Not checked",
    };
}
