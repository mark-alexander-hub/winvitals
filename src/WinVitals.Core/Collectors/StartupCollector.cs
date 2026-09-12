using System.Xml.Linq;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// What launches itself: startup entries and scheduled tasks.
///
/// Scheduled tasks are read straight from their XML definitions under
/// %WINDIR%\System32\Tasks rather than by parsing schtasks output. The XML is the
/// same on every machine; schtasks output is translated, so parsing it breaks on
/// any Windows that is not in English.
/// </summary>
public sealed class StartupCollector : ICollector
{
    public string Id => "startup";
    public string Name => "Startup & Scheduled Tasks";
    public string Blurb => "Programs that launch themselves at sign-in, and tasks scheduled to run or wake the machine.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        StartupPrograms(m);
        ScheduledTasks(m);
    }

    private static void StartupPrograms(ModuleResult m)
    {
        var entries = Wmi.Query("SELECT * FROM Win32_StartupCommand")
            .Select(s => (Name: s.Str("Name"), Command: s.Str("Command"), Where: s.Str("Location")))
            .Where(e => e.Name.Length > 0)
            .ToList();

        m.Fact("Programs starting at sign-in", entries.Count.ToString());

        var evidence = string.Join("\n", entries.Select(e => $"{e.Name,-32} [{e.Where}]\n    {e.Command}"));

        if (entries.Count >= 15)
        {
            m.Add(Severity.Advisory, "startup.many", $"{entries.Count} programs start automatically at sign-in",
                what: "These launch themselves every time you sign in: "
                      + string.Join(", ", entries.Take(8).Select(e => e.Name))
                      + (entries.Count > 8 ? $", and {entries.Count - 8} more." : "."),
                why: "Each one competes for disk and CPU during the first minute after sign-in, which is exactly "
                     + "when the machine feels slowest. Most updaters and 'helper' tray programs do not need to "
                     + "run constantly to do their job.",
                action: "Open Task Manager's Startup tab, sort by impact, and disable what you do not need at "
                        + "sign-in. Disabling a startup entry does not uninstall the program.",
                command: "taskmgr /0 /startup",
                evidence: evidence);
        }
        else
        {
            m.Ok("startup.count", $"{entries.Count} program(s) start at sign-in",
                "A reasonable number — this is not slowing your sign-in down.", evidence);
        }
    }

    private static void ScheduledTasks(ModuleResult m)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        if (!Directory.Exists(root))
        {
            m.Add(Severity.Unknown, "startup.tasks", "Could not read scheduled tasks",
                $"{root} is not accessible.");
            return;
        }

        var wakers = new List<(string Name, bool Enabled)>();
        var total = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
        }
        catch
        {
            m.Add(Severity.Unknown, "startup.tasks", "Could not enumerate scheduled tasks", "Access was denied.");
            return;
        }

        foreach (var file in files)
        {
            total++;
            try
            {
                var doc = XDocument.Load(file);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                var wake = doc.Root?.Element(ns + "Settings")?.Element(ns + "WakeToRun")?.Value;
                if (!string.Equals(wake, "true", StringComparison.OrdinalIgnoreCase)) continue;

                var enabledValue = doc.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
                var enabled = !string.Equals(enabledValue, "false", StringComparison.OrdinalIgnoreCase);

                var name = Path.GetRelativePath(root, file).Replace('\\', '/');
                wakers.Add((name, enabled));
            }
            catch
            {
                // Not every file under Tasks is a well-formed task definition.
            }
        }

        m.Fact("Scheduled tasks", total.ToString());

        var active = wakers.Where(w => w.Enabled).ToList();
        var evidence = wakers.Count == 0
            ? "No task definitions request WakeToRun."
            : string.Join("\n", wakers.OrderByDescending(w => w.Enabled)
                .Select(w => $"{(w.Enabled ? "enabled " : "disabled")}  {w.Name}"));

        if (active.Count > 0)
        {
            m.Add(Severity.Warning, "startup.wake-tasks",
                $"{active.Count} scheduled task(s) are allowed to wake this machine",
                what: "These tasks have \"wake the computer to run this task\" enabled: "
                      + string.Join(", ", active.Take(6).Select(a => a.Name))
                      + (active.Count > 6 ? $", and {active.Count - 6} more." : ""),
                why: "A task with this flag pulls the machine out of sleep at its scheduled time even if the "
                     + "lid is closed and it is in a bag. This is the most common answer to \"why was my laptop "
                     + "hot and flat when I took it out?\", and unlike wake timers these persist across power "
                     + "plan changes.",
                action: "Open Task Scheduler, find the task, and clear \"Wake the computer to run this task\" on "
                        + "its Conditions tab. Vendor updater tasks are usually safe to change; leave anything "
                        + "under Microsoft\\Windows alone unless you know what it does.",
                command: "taskschd.msc",
                evidence: evidence);
        }
        else if (wakers.Count > 0)
        {
            m.Ok("startup.wake-tasks", "No enabled task can wake this machine",
                $"{wakers.Count} task(s) request wake permission but all of them are disabled.", evidence);
        }
        else
        {
            m.Ok("startup.wake-tasks", "No scheduled task can wake this machine",
                $"None of the {total} scheduled tasks ask to wake the computer.", evidence);
        }
    }
}
