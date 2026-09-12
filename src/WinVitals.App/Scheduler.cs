using System.IO;
using System.Text.Json;
using WinVitals.Core;

namespace WinVitals.App;

/// <summary>What a scheduled check-up leaves behind for the dashboard to show.</summary>
public sealed record ScheduledSummary(
    DateTimeOffset When, int Score, int Critical, int Warning, int Advisory, int Ok, int Unknown, string ReportPath)
{
    public static ScheduledSummary? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ScheduledSummary)) return null;
            return JsonSerializer.Deserialize<ScheduledSummary>(File.ReadAllText(AppPaths.ScheduledSummary));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the last scheduled summary: {ex.Message}");
            return null;
        }
    }

    public static void Save(ScanResult scan, string reportPath)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Reports);
            var summary = new ScheduledSummary(
                scan.StartedAt, Session.Score(scan),
                scan.Count(Severity.Critical), scan.Count(Severity.Warning), scan.Count(Severity.Advisory),
                scan.Count(Severity.Ok), scan.Count(Severity.Unknown), reportPath);
            File.WriteAllText(AppPaths.ScheduledSummary, JsonSerializer.Serialize(summary));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the scheduled summary: {ex.Message}");
        }
    }
}

/// <summary>
/// A weekly check-up as a Windows scheduled task.
///
/// Created with "run only when the user is logged on" (no stored password) and
/// highest privileges, which the executable's manifest requires anyway. It points
/// at the executable's current location, so moving WinVitals.exe breaks the task —
/// the interface says so.
/// </summary>
public static class Scheduler
{
    public const string TaskName = "WinVitals weekly check-up";

    public static bool IsScheduled() =>
        Shell.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").Ok;

    public static (bool Ok, string Message) Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return (false, "Could not determine where WinVitals.exe is.");

        // Sunday 10:00 is when most home PCs are on and nobody is racing to get work done.
        var args = $"/Create /TN \"{TaskName}\" "
                   + $"/TR \"\\\"{exe}\\\" --check full --scheduled --no-open\" "
                   + "/SC WEEKLY /D SUN /ST 10:00 /RL HIGHEST /F";

        var res = Shell.Run("schtasks.exe", args);
        Log.Info($"Scheduler enable: ok={res.Ok} {res.Text}");
        return res.Ok
            ? (true, "WinVitals will check this PC every Sunday at 10:00 while you are signed in.")
            : (false, $"Windows refused to create the task: {res.Text}");
    }

    public static (bool Ok, string Message) Disable()
    {
        var res = Shell.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        Log.Info($"Scheduler disable: ok={res.Ok} {res.Text}");
        return res.Ok
            ? (true, "The weekly check-up is off.")
            : (false, $"Could not remove the task: {res.Text}");
    }

    /// <summary>Runs the scheduled check-up now, in the background, as the task would.</summary>
    public static (bool Ok, string Message) RunNow()
    {
        var res = Shell.Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        return res.Ok
            ? (true, "Started. The result appears here when it finishes, in about a minute.")
            : (false, $"Could not start the task: {res.Text}");
    }
}
