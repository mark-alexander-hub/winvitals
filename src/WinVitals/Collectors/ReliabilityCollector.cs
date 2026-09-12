using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Crashes, unexpected shutdowns and hardware errors over the last month.
///
/// The valuable one here is WHEA. Corrected hardware errors are logged silently,
/// cause no visible symptom until they stop being correctable, and are the earliest
/// warning that memory or a CPU is on its way out.
/// </summary>
public sealed class ReliabilityCollector : ICollector
{
    public string Id => "reliability";
    public string Name => "Crashes & Stability";
    public string Blurb => "Blue screens, unexpected shutdowns, hardware errors and repeat application crashes.";

    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        BlueScreens(m);
        UnexpectedShutdowns(m);
        HardwareErrors(m);
        AppCrashes(m);
        DiskErrors(m);
    }

    private static void BlueScreens(ModuleResult m)
    {
        var bugchecks = EventLogReader.Read("System", "Microsoft-Windows-WER-SystemErrorReporting",
            new[] { 1001 }, 20, Window);

        if (bugchecks.Count == 0)
        {
            m.Ok("reliability.bsod", "No blue screens in the last 30 days",
                "Windows recorded no bug checks.");
            return;
        }

        var evidence = string.Join("\n\n", bugchecks.Select(b => $"{b.When:yyyy-MM-dd HH:mm}  {b.OneLine}"));

        m.Add(bugchecks.Count >= 3 ? Severity.Critical : Severity.Warning, "reliability.bsod",
            $"{bugchecks.Count} blue screen(s) in the last 30 days",
            what: $"The most recent was on {bugchecks[0].When:d MMMM 'at' HH:mm}.",
            why: bugchecks.Count >= 3
                ? "Repeated bug checks mean something is genuinely broken — most often a driver, failing "
                  + "memory, or an overheating component. This will not settle down on its own."
                : "An isolated bug check is usually a driver misbehaving once. A pattern means hardware.",
            action: "The bug check code in the evidence below is the thing to search for; it names the fault. "
                    + "If several have different codes, suspect memory: run the built-in memory test.",
            command: "mdsched.exe",
            evidence: evidence);
    }

    private static void UnexpectedShutdowns(ModuleResult m)
    {
        var kp41 = EventLogReader.Read("System", "Microsoft-Windows-Kernel-Power", new[] { 41 }, 20, Window);
        if (kp41.Count == 0)
        {
            m.Ok("reliability.kp41", "No unexpected shutdowns", "The machine has always been shut down cleanly.");
            return;
        }

        m.Add(kp41.Count >= 3 ? Severity.Warning : Severity.Advisory, "reliability.kp41",
            $"{kp41.Count} unexpected shutdown(s) in the last 30 days",
            what: $"Windows restarted without shutting down first — most recently on {kp41[0].When:d MMMM 'at' HH:mm}.",
            why: "This event means the machine lost power or froze hard, rather than crashing with a blue "
                 + "screen. On a laptop the usual causes are a failing battery that cannot hold under load, "
                 + "overheating, or a power supply problem. It is also what a forced power-button hold looks like.",
            action: "If you held the power button on these occasions, ignore this. If not, check the battery "
                    + "health section above and whether the machine is running hot.",
            evidence: string.Join("\n", kp41.Select(e => $"{e.When:yyyy-MM-dd HH:mm}  Kernel-Power 41")));
    }

    private static void HardwareErrors(ModuleResult m)
    {
        var whea = EventLogReader.Read("System", "Microsoft-Windows-WHEA-Logger", null, 50, Window);
        if (whea.Count == 0)
        {
            m.Ok("reliability.whea", "No hardware errors logged",
                "The Windows Hardware Error Architecture log is clean for the last 30 days.");
            return;
        }

        // 17/19/47 are corrected: the hardware caught and fixed it. 18 is uncorrectable.
        var fatal = whea.Where(e => e.Id == 18).ToList();
        var corrected = whea.Where(e => e.Id != 18).ToList();
        var evidence = string.Join("\n", whea.Take(20).Select(e => $"{e.When:yyyy-MM-dd HH:mm}  id={e.Id}  {Truncate(e.OneLine, 140)}"));

        if (fatal.Count > 0)
        {
            m.Add(Severity.Critical, "reliability.whea-fatal",
                $"{fatal.Count} uncorrectable hardware error(s) logged",
                what: $"WHEA recorded {fatal.Count} fatal hardware error(s), most recently on {fatal[0].When:d MMMM 'at' HH:mm}.",
                why: "An uncorrectable error means a hardware component produced a result it could not fix. "
                     + "This is the machine telling you a physical part is failing — usually memory, the CPU, "
                     + "or a PCIe device.",
                action: "Back up now. Run the memory test, and if the machine is under warranty, quote these "
                        + "events to the vendor: they are hard evidence of a hardware fault.",
                command: "mdsched.exe",
                evidence: evidence);
        }
        else if (corrected.Count >= 20)
        {
            m.Add(Severity.Warning, "reliability.whea-corrected",
                $"{corrected.Count} corrected hardware errors in 30 days",
                what: $"WHEA logged {corrected.Count} errors that the hardware corrected by itself.",
                why: "Corrected errors cause no visible symptom, which is exactly why they matter: they are "
                     + "the early stage of a component failing, logged silently for weeks before anything "
                     + "user-visible happens. A steady stream of them is not normal.",
                action: "Run the memory test, and check that the machine's vents are clear and it is not "
                        + "running hot. If the count keeps climbing, treat the hardware as suspect.",
                command: "mdsched.exe",
                evidence: evidence);
        }
        else
        {
            m.Add(Severity.Advisory, "reliability.whea-corrected",
                $"{corrected.Count} corrected hardware error(s) logged",
                what: $"WHEA logged {corrected.Count} error(s) that the hardware corrected itself.",
                why: "A small number of corrected errors is common and not a fault on its own. It is worth "
                     + "knowing the count so you can tell if it starts rising.",
                action: "Nothing to do. Re-check if the machine starts behaving oddly.",
                evidence: evidence);
        }
    }

    private static void AppCrashes(ModuleResult m)
    {
        var crashes = EventLogReader.Read("Application", "Application Error", new[] { 1000 }, 100, Window);
        if (crashes.Count == 0)
        {
            m.Ok("reliability.apps", "No repeated application crashes", "No application crash reports in 30 days.");
            return;
        }

        // Group by the faulting executable, which is the first token of the message.
        var byApp = crashes
            .Select(c => (Crash: c, App: FirstWord(c.OneLine)))
            .Where(x => x.App is not null)
            .GroupBy(x => x.App!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.Key, Count: g.Count(), Last: g.Max(x => x.Crash.When)))
            .OrderByDescending(g => g.Count)
            .ToList();

        m.Fact("Application crashes (30 days)", crashes.Count.ToString());

        var repeat = byApp.Where(a => a.Count >= 5).ToList();
        var evidence = string.Join("\n", byApp.Take(15).Select(a => $"{a.Count,4}x  {a.App,-40} last {a.Last:yyyy-MM-dd HH:mm}"));

        if (repeat.Count > 0)
        {
            m.Add(Severity.Advisory, "reliability.apps",
                $"{repeat[0].App} has crashed {repeat[0].Count} times",
                what: string.Join("; ", repeat.Take(3).Select(a => $"{a.App} crashed {a.Count} times")) + " in the last 30 days.",
                why: "One crash is noise. A program crashing this often usually has a corrupt installation, a "
                     + "bad add-in, or a conflict with a driver.",
                action: "Update or reinstall the program at the top of that list. If it is a browser, try it "
                        + "with extensions disabled.",
                evidence: evidence);
        }
        else
        {
            m.Ok("reliability.apps", "No application is crashing repeatedly",
                $"{crashes.Count} crash(es) spread across {byApp.Count} program(s), none of them frequent.", evidence);
        }
    }

    private static void DiskErrors(ModuleResult m)
    {
        // Ask for errors by level, not by event ID. NTFS reuses its ID range for healthy
        // status notices — event 98 is literally "Volume is healthy. No action is needed."
        // — so an ID list quietly turns good news into a disk-failure warning.
        var errorLevels = new[] { 1, 2 };
        var ntfs = EventLogReader.Read("System", "Microsoft-Windows-Ntfs", null, 20, Window, errorLevels);
        var disk = EventLogReader.Read("System", "disk", null, 20, Window, errorLevels);
        var storSpace = EventLogReader.Read("System", "Microsoft-Windows-StorDiag", null, 10, Window, errorLevels);
        var all = ntfs.Concat(disk).Concat(storSpace).OrderByDescending(e => e.When).ToList();

        if (all.Count == 0)
        {
            m.Ok("reliability.disk-errors", "No disk or filesystem errors logged",
                "Nothing in the last 30 days.");
            return;
        }

        // Name the devices. A controller error on an external disk is a different
        // problem from one on the system drive, and the user cannot tell which they
        // have from a bare count.
        var devices = all
            .Select(e => System.Text.RegularExpressions.Regex.Match(e.OneLine, @"\\Device\\Harddisk\w*\d*(\\DR\d+)?"))
            .Where(match => match.Success)
            .Select(match => match.Value)
            .Distinct()
            .ToList();

        m.Add(Severity.Warning, "reliability.disk-errors",
            $"{all.Count} disk or filesystem error(s) in the last 30 days",
            what: $"The most recent was on {all[0].When:d MMMM 'at' HH:mm}."
                  + (devices.Count > 0
                      ? $" Affected device(s): {string.Join(", ", devices)}."
                        + " Harddisk0 is normally the system drive; a higher number is usually an external "
                        + "or removable disk."
                      : ""),
            why: "These events mean Windows had trouble reading from or writing to the disk, or found "
                 + "filesystem corruption. They frequently precede a drive failure, and they are logged long "
                 + "before anything visible goes wrong.",
            action: "Check the disk health section above, then run a read-only filesystem check. Make sure "
                    + "your backups are current before doing anything else.",
            command: "chkdsk C: /scan",
            evidence: string.Join("\n", all.Take(15).Select(e =>
                $"{e.When:yyyy-MM-dd HH:mm}  {e.Provider} id={e.Id}  {Truncate(e.OneLine, 120)}")));
    }

    private static string? FirstWord(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0) return null;
        var word = trimmed.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return word is not null && word.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? word : null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
