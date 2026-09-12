using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Windows servicing state: patches, whether a restart is being deferred, and
/// whether there is actually a restore point to fall back to.
///
/// The restore point check is deliberately sceptical. System Protection can be
/// switched on, report no error, and still be holding nothing usable — and Windows
/// silently refuses to create a second restore point within 24 hours of the last
/// one, so "I made one before I started" is often not true.
/// </summary>
public sealed class UpdateCollector : ICollector
{
    public string Id => "updates";
    public string Name => "Updates & Recovery";
    public string Blurb => "Patch level, deferred restarts, and whether you have a restore point that actually exists.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        Patches(m);
        PendingReboot(m);
        RestorePoints(ctx, m);
    }

    private static void Patches(ModuleResult m)
    {
        var hotfixes = Wmi.Query("SELECT HotFixID, InstalledOn, Description FROM Win32_QuickFixEngineering");
        if (hotfixes.Count == 0)
        {
            m.Add(Severity.Unknown, "updates.patches", "No update history is available",
                "Win32_QuickFixEngineering returned nothing.",
                why: "Normal on machines where updates arrive as full builds rather than individual patches.",
                action: "Check Windows Update directly.",
                command: "start ms-settings:windowsupdate");
            return;
        }

        var dated = hotfixes
            .Select(h => (Id: h.Str("HotFixID"), When: ParseInstalledOn(h.Str("InstalledOn"))))
            .Where(h => h.When.HasValue)
            .OrderByDescending(h => h.When)
            .ToList();

        m.Fact("Updates installed", hotfixes.Count.ToString());

        if (dated.Count == 0) return;

        var newest = dated[0];
        var age = DateTime.Now - newest.When!.Value;
        m.Fact("Most recent update", $"{newest.Id} on {newest.When:d MMMM yyyy}",
            $"{(int)age.TotalDays} days ago");

        var evidence = string.Join("\n", dated.Take(10).Select(d => $"{d.When:yyyy-MM-dd}  {d.Id}"));

        // Windows ships security fixes monthly, so nothing for two months means updates
        // are not arriving, not that none were released.
        if (age.TotalDays > 75)
        {
            m.Add(Severity.Warning, "updates.stale", $"No updates have installed for {(int)age.TotalDays} days",
                what: $"The last update recorded is {newest.Id}, installed {newest.When:d MMMM yyyy}.",
                why: "Windows publishes security fixes every month. A gap this long means updates are failing, "
                     + "paused, or blocked, and the machine is missing patches that have been public for weeks.",
                action: "Open Windows Update and check for errors. If it is paused, resume it.",
                command: "start ms-settings:windowsupdate",
                evidence: evidence);
        }
        else
        {
            m.Ok("updates.recent", "Updates are arriving",
                $"Most recent: {newest.Id} on {newest.When:d MMMM yyyy}.", evidence);
        }
    }

    private static DateTime? ParseInstalledOn(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // This property is inconsistent: sometimes a locale-formatted date, sometimes
        // a WMI datetime, sometimes a hex FILETIME. Try the common shapes.
        if (DateTime.TryParse(raw, out var parsed)) return parsed;
        try { return System.Management.ManagementDateTimeConverter.ToDateTime(raw); } catch { }
        return null;
    }

    private static void PendingReboot(ModuleResult m)
    {
        var reasons = new List<string>();

        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Component Based Servicing has staged changes");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update is waiting to finish an install");
        if (ValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
            reasons.Add("files are queued to be replaced on the next boot");

        if (reasons.Count == 0)
        {
            m.Ok("updates.reboot", "No restart is pending", "Nothing is waiting for a reboot to finish.");
            return;
        }

        m.Add(Severity.Warning, "updates.reboot", "A restart is needed to finish installing updates",
            what: "Windows is waiting for a restart because " + string.Join(", and ", reasons) + ".",
            why: "Until the machine restarts, those updates are only half-applied. Security fixes in this state "
                 + "are not protecting you yet, and further updates will often refuse to install.",
            action: "Restart when you can. If you have restarted recently and this persists, an update is "
                    + "failing and rolling back each time.",
            evidence: string.Join("\n", reasons));
    }

    private static void RestorePoints(ScanContext ctx, ModuleResult m)
    {
        // root\default:SystemRestore returns "access denied" to a standard user, and a
        // swallowed denial looks exactly like an empty result. Reporting "you have no
        // restore points" when the truth is "I was not allowed to look" is the worst
        // thing a diagnostic tool can do, so this check refuses to guess.
        if (!ctx.Elevated)
        {
            m.NeedsAdmin("updates.restore", "Cannot check whether you have a restore point",
                "Reading System Restore needs administrator rights. Without it there is no way to tell an "
                + "empty restore history apart from a query that was refused.");
            return;
        }

        var points = Wmi.Query("SELECT * FROM SystemRestore", @"root\default")
            .Select(p => (Seq: p.Num("SequenceNumber"), Desc: p.Str("Description"), When: p.Date("CreationTime")))
            .OrderByDescending(p => p.When)
            .ToList();

        if (points.Count == 0)
        {
            m.Add(Severity.Warning, "updates.restore-none", "There are no system restore points",
                what: "System Restore is holding nothing on this machine.",
                why: "System Protection is off by default on many Windows 11 installs. People discover this at "
                     + "the worst possible moment: after a bad driver or update, when the recovery option they "
                     + "assumed was there turns out to be empty.",
                action: "Turn on System Protection for the system drive and create a restore point now. Note "
                        + "that it is not a backup — it protects system files and settings, not your documents.",
                command: "SystemPropertiesProtection.exe",
                evidence: "root\\default:SystemRestore returned no instances.");
            return;
        }

        var newest = points[0];
        var age = newest.When.HasValue ? DateTime.Now - newest.When.Value : (TimeSpan?)null;

        m.Fact("Restore points", points.Count.ToString(),
            newest.When.HasValue ? $"newest {newest.When:d MMM yyyy}" : null);

        var evidence = string.Join("\n", points.Take(10).Select(p =>
            $"{(p.When.HasValue ? p.When.Value.ToString("yyyy-MM-dd HH:mm") : "unknown date")}  #{p.Seq}  {p.Desc}"));

        if (age.HasValue && age.Value.TotalDays > 60)
        {
            m.Add(Severity.Advisory, "updates.restore-old",
                $"The newest restore point is {(int)age.Value.TotalDays} days old",
                what: $"{points.Count} restore point(s) exist; the most recent is from {newest.When:d MMMM yyyy}.",
                why: "Rolling back to a point that old would undo months of settings and updates, so in practice "
                     + "it is not a usable safety net.",
                action: "Create a fresh restore point before your next driver or firmware change. Be aware that "
                        + "Windows silently refuses to create one if another was made in the last 24 hours — so "
                        + "check the date afterwards rather than assuming it worked.",
                command: "powershell -Command \"Checkpoint-Computer -Description 'Manual' -RestorePointType MODIFY_SETTINGS\"",
                evidence: evidence);
        }
        else
        {
            m.Ok("updates.restore", $"{points.Count} restore point(s) available",
                newest.When.HasValue
                    ? $"The most recent is from {newest.When:d MMMM yyyy}."
                    : "Dates were not reported.",
                evidence);
        }
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }

    private static bool ValueExists(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            var v = key?.GetValue(name);
            return v is string[] arr ? arr.Length > 0 : v is not null;
        }
        catch { return false; }
    }
}
