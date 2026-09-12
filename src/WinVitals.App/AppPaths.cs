using System.IO;

namespace WinVitals.App;

/// <summary>Where WinVitals keeps its own files. Everything lives under one folder.</summary>
public static class AppPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVitals");

    /// <summary>Reports written by scheduled check-ups.</summary>
    public static string Reports => Path.Combine(Root, "reports");

    /// <summary>Summary of the most recent scheduled check-up, read by the dashboard.</summary>
    public static string ScheduledSummary => Path.Combine(Reports, "latest.json");

    /// <summary>The boot time recorded before the user started turning off startup programs.</summary>
    public static string SpeedupSnapshot => Path.Combine(Root, "speedup.json");
}
