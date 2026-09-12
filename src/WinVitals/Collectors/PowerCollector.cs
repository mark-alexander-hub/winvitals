using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Why the machine will not sleep, why it wakes up on its own, and whether it
/// bounces straight back out of sleep.
///
/// The last one is the reason this module exists. "Laptop wakes up in my bag" and
/// "laptop will not stay asleep" are among the most common Windows complaints and
/// the answer is spread across four different powercfg subcommands plus the event
/// log, three of which need elevation. Nothing joins them up.
/// </summary>
public sealed class PowerCollector : ICollector
{
    public string Id => "power";
    public string Name => "Power & Sleep";
    public string Blurb => "Whether this machine can sleep, what is keeping it awake, and what wakes it up.";

    // "Sleep Time: 2026-09-12T12:26:18.909209300Z", padded with bidi marks by the event log.
    private static readonly Regex HexIndex = new(@"0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        SleepModel(m);
        Timeouts(m);
        Requests(ctx, m);
        WakeTimers(ctx, m);
        WakeArmedDevices(m);
        SleepWakeHistory(m);
    }

    // ---------------------------------------------------------------- model

    private static void SleepModel(ModuleResult m)
    {
        var res = Shell.PowerCfg("/a");
        if (!res.Ok && res.Text.Length == 0)
        {
            m.Add(Severity.Unknown, "power.states", "Could not read supported sleep states",
                "powercfg /a returned nothing.", evidence: res.StdErr);
            return;
        }

        var (available, unavailable) = ParseAvailability(res.Text);

        var modern = available.Any(s => s.Contains("S0 Low Power Idle", StringComparison.OrdinalIgnoreCase));
        var s3 = available.Any(s => s.Contains("(S3)", StringComparison.OrdinalIgnoreCase));
        var hibernate = available.Any(s => s.StartsWith("Hibernate", StringComparison.OrdinalIgnoreCase));

        m.Fact("Sleep states available", available.Count > 0 ? string.Join(", ", available) : "none");

        if (modern)
        {
            m.Add(Severity.Advisory, "power.modern-standby", "This machine uses Modern Standby, not real sleep",
                what: "The firmware reports S0 Low Power Idle (Modern Standby). Classic S3 sleep is not used.",
                why: "Modern Standby keeps Windows lightly running while the lid is shut so it can collect mail "
                     + "and updates. That is why these machines get warm in a bag and lose battery overnight: "
                     + "any app that misbehaves keeps the machine partly awake, and there is no hard 'off' state "
                     + "the way S3 gave you.",
                action: "Run the built-in Modern Standby report to see exactly what kept it awake overnight. "
                        + "It writes an HTML file you can open in a browser.",
                command: "powercfg /sleepstudy /output %USERPROFILE%\\Desktop\\sleepstudy.html",
                evidence: res.Text);
        }
        else if (s3)
        {
            m.Ok("power.s3", "This machine supports real sleep (S3)",
                "Classic S3 standby is available, so a closed lid genuinely suspends the machine.", res.Text);
        }
        else
        {
            m.Add(Severity.Warning, "power.no-sleep", "This machine cannot sleep at all",
                what: "Neither S3 standby nor Modern Standby is available.",
                why: "With no sleep state the machine can only stay on, hibernate, or shut down. Closing the "
                     + "lid will not save power.",
                action: "This is usually a firmware setting or a virtualization feature. Check the vendor's "
                        + "BIOS setup for a sleep-state option, and see whether the hypervisor note below applies.",
                evidence: res.Text);
        }

        // A hypervisor (Hyper-V, WSL2, Windows Sandbox, or memory integrity/VBS) takes
        // over the sleep states it needs. People chase this for hours without being told.
        var hyper = unavailable.FirstOrDefault(u => u.Reason.Contains("hypervisor", StringComparison.OrdinalIgnoreCase));
        if (hyper.State is not null)
        {
            m.Add(Severity.Advisory, "power.hypervisor", "A hypervisor is holding one of the sleep states",
                what: $"\"{hyper.State}\" is unavailable because the hypervisor does not support it.",
                why: "Something is running the Windows hypervisor: Hyper-V, WSL2, Windows Sandbox, Docker with "
                     + "the WSL backend, or Core Isolation / Memory Integrity. That is usually fine and worth "
                     + "keeping, but it does remove this sleep state, and no amount of power-plan tweaking will "
                     + "bring it back while the hypervisor is loaded.",
                action: "Leave it alone unless you actively need that sleep state. If you do, find which feature "
                        + "turned the hypervisor on before disabling anything: turning off Memory Integrity "
                        + "weakens the machine's defences.",
                command: "bcdedit /enum {current} | findstr hypervisorlaunchtype",
                evidence: string.Join("\n", unavailable.Select(u => $"{u.State}: {u.Reason}")));
        }

        if (!hibernate)
        {
            m.Add(Severity.Advisory, "power.no-hibernate", "Hibernate is turned off",
                what: "Hibernate is not available on this machine.",
                why: "Without hibernate, a laptop that runs out of battery while asleep loses whatever was open. "
                     + "Hibernate writes memory to disk so a flat battery costs you nothing.",
                action: "Turn it on if you want a safety net for long unplugged periods. It reserves disk space "
                        + "roughly equal to a fraction of your RAM.",
                command: "powercfg /hibernate on",
                undo: "powercfg /hibernate off",
                evidence: res.Text);
        }
    }

    internal static (List<string> Available, List<(string State, string Reason)> Unavailable)
        ParseAvailability(string text)
    {
        var available = new List<string>();
        var unavailable = new List<(string, string)>();
        var inAvailable = false;
        string? pending = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("are available on this system", StringComparison.OrdinalIgnoreCase))
            {
                inAvailable = true; pending = null; continue;
            }
            if (line.Contains("are not available on this system", StringComparison.OrdinalIgnoreCase))
            {
                inAvailable = false; pending = null; continue;
            }
            if (line.Trim().Length == 0) continue;

            // Reasons come indented under the state they belong to, with a tab.
            var isReason = line.StartsWith("\t") || line.StartsWith("      ");
            var value = line.Trim();

            if (isReason && pending is not null)
            {
                unavailable.Add((pending, value));
                pending = null;
            }
            else if (inAvailable)
            {
                available.Add(value);
            }
            else
            {
                // Some states are listed with no reason line at all.
                if (pending is not null) unavailable.Add((pending, "no reason given"));
                pending = value;
            }
        }

        if (pending is not null) unavailable.Add((pending, "no reason given"));
        return (available, unavailable);
    }

    // ------------------------------------------------------------- timeouts

    private static void Timeouts(ModuleResult m)
    {
        var sleepAc = ReadSetting("SUB_SLEEP", "STANDBYIDLE", out var sleepDc);
        var dispAc = ReadSetting("SUB_VIDEO", "VIDEOIDLE", out var dispDc);
        var hibAc = ReadSetting("SUB_SLEEP", "HIBERNATEIDLE", out var hibDc);

        m.Fact("Sleep after", $"plugged in: {Timeout(sleepAc)} / on battery: {Timeout(sleepDc)}");
        m.Fact("Display off after", $"plugged in: {Timeout(dispAc)} / on battery: {Timeout(dispDc)}");
        m.Fact("Hibernate after", $"plugged in: {Timeout(hibAc)} / on battery: {Timeout(hibDc)}");

        var evidence = $"Sleep after      AC={Timeout(sleepAc)}  DC={Timeout(sleepDc)}\n"
                     + $"Display off      AC={Timeout(dispAc)}  DC={Timeout(dispDc)}\n"
                     + $"Hibernate after  AC={Timeout(hibAc)}  DC={Timeout(hibDc)}";

        if (sleepAc == 0 && sleepDc == 0)
        {
            m.Add(Severity.Advisory, "power.sleep-never", "Automatic sleep is switched off entirely",
                what: "\"Sleep after\" is set to Never, both plugged in and on battery.",
                why: "This is the single most common reason a laptop never sleeps, and it is a setting rather "
                     + "than a fault. Plenty of people set it deliberately, so WinVitals will not call it a "
                     + "problem. But if you did not set it and your machine stays awake, this is your answer.",
                action: "If you want automatic sleep back, the command below sets 15 minutes on battery and "
                        + "30 minutes plugged in. If Never was deliberate, ignore this.",
                command: "powercfg /change standby-timeout-ac 30\npowercfg /change standby-timeout-dc 15",
                undo: "powercfg /change standby-timeout-ac 0\npowercfg /change standby-timeout-dc 0",
                evidence: evidence);
        }
        else if (sleepDc == 0)
        {
            m.Add(Severity.Advisory, "power.sleep-never-battery", "The machine never sleeps on battery",
                what: $"\"Sleep after\" is Never on battery (plugged in it is {Timeout(sleepAc)}).",
                why: "On battery this drains the machine flat in a bag.",
                action: "Set a battery sleep timeout unless this is deliberate.",
                command: "powercfg /change standby-timeout-dc 15",
                undo: "powercfg /change standby-timeout-dc 0",
                evidence: evidence);
        }
        else
        {
            m.Ok("power.sleep-timeouts", "Sleep timeouts are set",
                $"Sleeps after {Timeout(sleepAc)} plugged in and {Timeout(sleepDc)} on battery.", evidence);
        }
    }

    private static string Timeout(int seconds) =>
        seconds <= 0 ? "Never" :
        seconds % 3600 == 0 ? $"{seconds / 3600} h" :
        seconds >= 3600 ? $"{seconds / 3600} h {(seconds % 3600) / 60} min" :
        $"{seconds / 60} min";

    /// <summary>Reads one power setting's AC and DC value in seconds. -1 when unreadable.</summary>
    private static int ReadSetting(string subgroup, string setting, out int dc)
    {
        dc = -1;
        var res = Shell.PowerCfg($"/q SCHEME_CURRENT {subgroup} {setting}");
        if (!res.Ok) return -1;

        int ac = -1;
        foreach (var line in res.Lines)
        {
            var match = HexIndex.Match(line);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                continue;

            if (line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase)) ac = v;
            else if (line.Contains("Current DC Power Setting Index", StringComparison.OrdinalIgnoreCase)) dc = v;
        }
        return ac;
    }

    // ------------------------------------------------------------- requests

    private static void Requests(ScanContext ctx, ModuleResult m)
    {
        if (!ctx.Elevated)
        {
            m.NeedsAdmin("power.requests", "Cannot see what is holding the machine awake",
                "Listing active power requests needs administrator rights. This is the check that names the "
                + "exact app or driver blocking sleep right now.");
            return;
        }

        var res = Shell.PowerCfg("/requests");
        if (!res.Ok)
        {
            m.Add(Severity.Unknown, "power.requests", "Could not read power requests",
                "powercfg /requests failed.", evidence: res.Text);
            return;
        }

        var sections = ParseRequests(res.Text);
        var active = sections.Where(s => s.Value.Count > 0).ToList();

        if (active.Count == 0)
        {
            m.Ok("power.requests", "Nothing is currently blocking sleep",
                "No application, driver or service is holding a power request right now.", res.Text);
            return;
        }

        foreach (var (section, entries) in active)
        {
            var explain = section switch
            {
                "DISPLAY" => "keeping the screen on",
                "SYSTEM" => "stopping the machine from sleeping",
                "AWAYMODE" => "keeping the machine running with the screen off",
                "EXECUTION" => "telling Windows it is busy and must not be interrupted",
                "PERFBOOST" => "asking for sustained performance",
                "ACTIVELOCKSCREEN" => "keeping the lock screen active",
                _ => "holding a power request",
            };

            m.Add(Severity.Warning, $"power.request.{section.ToLowerInvariant()}",
                $"Something is {explain}",
                what: $"{entries.Count} active {section} request: " + string.Join("; ", entries.Select(Describe)),
                why: "A power request is an app or driver telling Windows \"do not sleep yet\". Media playback, "
                     + "an open video call, a file being copied over the network, or a stuck audio stream all do "
                     + "this. Audio drivers are the classic offender: they hold the request long after the sound "
                     + "has stopped.",
                action: "Close the app named above and re-run this scan. If it is a driver that will not let go, "
                        + "you can override it permanently with the command below.",
                command: OverrideCommand(section, entries),
                undo: "powercfg /requestsoverride <TYPE> \"<name>\"   :: with no trailing request types, clears the override",
                evidence: res.Text);
        }
    }

    private static string Describe(string entry)
    {
        var kind = entry.StartsWith("[DRIVER]") ? "driver"
            : entry.StartsWith("[PROCESS]") ? "app"
            : entry.StartsWith("[SERVICE]") ? "service"
            : "requester";

        var cleaned = Regex.Replace(entry, @"^\[(DRIVER|PROCESS|SERVICE)\]\s*", "");
        // Process requests come through as full device paths; the executable is the useful part.
        var exe = Regex.Match(cleaned, @"[^\\]+\.exe", RegexOptions.IgnoreCase);
        if (exe.Success) cleaned = exe.Value;

        return $"{kind} \"{cleaned.Trim()}\"";
    }

    private static string? OverrideCommand(string section, List<string> entries)
    {
        var first = entries.FirstOrDefault();
        if (first is null) return null;

        var kind = first.StartsWith("[DRIVER]") ? "DRIVER"
            : first.StartsWith("[SERVICE]") ? "SERVICE"
            : "PROCESS";

        var name = Regex.Replace(first, @"^\[(DRIVER|PROCESS|SERVICE)\]\s*", "").Split('\n')[0].Trim();
        var exe = Regex.Match(name, @"[^\\]+\.exe", RegexOptions.IgnoreCase);
        if (exe.Success) name = exe.Value;

        return $"powercfg /requestsoverride {kind} \"{name}\" {section}";
    }

    internal static Dictionary<string, List<string>> ParseRequests(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (Regex.IsMatch(trimmed, @"^[A-Z]+:$"))
            {
                current = trimmed.TrimEnd(':');
                sections[current] = new List<string>();
                continue;
            }

            if (current is null) continue;
            if (trimmed.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;

            // Continuation lines (the human-readable reason) attach to the entry above.
            if (!trimmed.StartsWith("[") && sections[current].Count > 0)
                sections[current][^1] += " — " + trimmed;
            else
                sections[current].Add(trimmed);
        }

        return sections;
    }

    // ----------------------------------------------------------- wake timers

    private static void WakeTimers(ScanContext ctx, ModuleResult m)
    {
        if (!ctx.Elevated)
        {
            m.NeedsAdmin("power.waketimers", "Cannot see scheduled wake-ups",
                "Listing wake timers needs administrator rights. This is the check that explains a machine "
                + "waking at the same time every night.");
            return;
        }

        var res = Shell.PowerCfg("/waketimers");
        var text = res.Text;

        if (text.Contains("no active wake timers", StringComparison.OrdinalIgnoreCase))
        {
            m.Ok("power.waketimers", "Nothing is scheduled to wake this machine",
                "No active wake timers are set.", text);
            return;
        }

        var timers = text.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.StartsWith("Timer set by", StringComparison.OrdinalIgnoreCase)
                        || l.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (timers.Count == 0)
        {
            m.Ok("power.waketimers", "No wake timers reported", text, text);
            return;
        }

        m.Add(Severity.Warning, "power.waketimers", "Something is scheduled to wake this machine up",
            what: $"{timers.Count(t => t.StartsWith("Timer", StringComparison.OrdinalIgnoreCase))} active wake timer(s) are set.",
            why: "A wake timer pulls the machine out of sleep at a set time, whether or not the lid is open. "
                 + "Windows Update maintenance is the usual source. In a bag, this means the fan spins up "
                 + "inside a closed case.",
            action: "If you do not want scheduled wake-ups at all, turn off wake timers for the current power "
                    + "plan. Update maintenance will then wait until the machine is awake anyway.",
            command: "powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0\n"
                     + "powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0\n"
                     + "powercfg /setactive SCHEME_CURRENT",
            undo: "powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 1\n"
                  + "powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 1\n"
                  + "powercfg /setactive SCHEME_CURRENT",
            evidence: text);
    }

    private static void WakeArmedDevices(ModuleResult m)
    {
        var res = Shell.PowerCfg("/devicequery wake_armed");
        var text = res.Text;

        if (text.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase) || text.Trim().Length == 0)
        {
            m.Ok("power.wake-armed", "No device can wake this machine",
                "Nothing is armed to wake the system — not the keyboard, mouse, or network card.", text);
            return;
        }

        var devices = res.Lines.Where(l => !l.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToList();
        var network = devices.Where(d =>
            d.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("GbE", StringComparison.OrdinalIgnoreCase)).ToList();

        m.Fact("Devices able to wake the machine", string.Join(", ", devices));

        var severity = network.Count > 0 ? Severity.Advisory : Severity.Ok;
        m.Add(severity, "power.wake-armed", $"{devices.Count} device(s) can wake this machine",
            what: "These devices are armed to wake the system: " + string.Join(", ", devices) + ".",
            why: network.Count > 0
                ? "A network adapter in that list means Wake-on-LAN is active. Broadcast traffic on an office "
                  + "or hotel network can then wake the machine repeatedly, including inside a bag."
                : "A mouse or keyboard waking the machine is normal. A twitchy mouse on a desk is a common "
                  + "cause of a machine that will not stay asleep.",
            action: network.Count > 0
                ? $"If the machine wakes on its own, disarm the network adapter: {string.Join(", ", network)}."
                : "If the machine wakes when nobody touches it, disarm the device with the command below.",
            command: $"powercfg /devicedisablewake \"{(network.FirstOrDefault() ?? devices[0])}\"",
            undo: $"powercfg /deviceenablewake \"{(network.FirstOrDefault() ?? devices[0])}\"",
            evidence: text);
    }

    // -------------------------------------------------------- sleep history

    /// <summary>
    /// Pairs each "entering sleep" with the wake that followed and looks for two
    /// patterns: sleep that does not stick, and wakes Windows cannot attribute.
    /// </summary>
    private static void SleepWakeHistory(ModuleResult m)
    {
        var wakes = EventLogReader.Read("System", "Microsoft-Windows-Power-Troubleshooter",
            new[] { 1 }, 20, TimeSpan.FromDays(14));

        var sleeps = EventLogReader.Read("System", "Microsoft-Windows-Kernel-Power",
            new[] { 42 }, 20, TimeSpan.FromDays(14));

        if (wakes.Count == 0 && sleeps.Count == 0)
        {
            m.Add(Severity.Unknown, "power.history", "No sleep or wake history found",
                "The System event log holds no sleep/wake records for the last 14 days.",
                why: "Either the machine has not slept recently, or the log was cleared.",
                evidence: "Queried Microsoft-Windows-Power-Troubleshooter id 1 and Microsoft-Windows-Kernel-Power id 42.");
            return;
        }

        var bounces = new List<(DateTime Slept, DateTime Woke, TimeSpan Held, string Source)>();
        var realWakes = 0;
        var unknownSources = 0;
        var timeline = new StringBuilder();

        foreach (var w in wakes)
        {
            var sleepTime = ParseEventTime(w.Field("Sleep Time"));
            var wakeTime = ParseEventTime(w.Field("Wake Time"));
            var source = Clean(w.Field("Wake Source")) ?? "not recorded";

            // Windows logs a "wake" when it surfaces only to drop from sleep into
            // hibernate. Sleep time equals wake time and nothing actually woke up.
            // Counting these as wakes would report a phantom zero-second bounce and
            // skew the wake-source statistics.
            if (IsDozeTransition(source))
            {
                timeline.AppendLine(
                    $"{(sleepTime ?? w.When.ToUniversalTime()).ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    + $"  ·  dropped from sleep into hibernate ({source})");
                continue;
            }

            realWakes++;
            if (source.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) unknownSources++;

            if (sleepTime.HasValue && wakeTime.HasValue)
            {
                var held = wakeTime.Value - sleepTime.Value;
                timeline.AppendLine(
                    $"slept {sleepTime.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}  →  woke "
                    + $"{wakeTime.Value.ToLocalTime():HH:mm:ss}  after {Human(held)}  (source: {source})");

                if (held.TotalSeconds is >= 1 and < 60)
                    bounces.Add((sleepTime.Value.ToLocalTime(), wakeTime.Value.ToLocalTime(), held, source));
            }
            else
            {
                timeline.AppendLine($"{w.When:yyyy-MM-dd HH:mm:ss}  woke (source: {source})");
            }
        }

        foreach (var s in sleeps.Take(5))
            timeline.AppendLine($"{s.When:yyyy-MM-dd HH:mm:ss}  entering sleep — {Clean(s.Field("Sleep Reason")) ?? "reason not recorded"}");

        // The headline check: sleep that does not stick.
        if (bounces.Count >= 2)
        {
            var worst = bounces.OrderBy(b => b.Held).First();
            m.Add(Severity.Warning, "power.sleep-bounce", "This machine wakes up seconds after going to sleep",
                what: $"{bounces.Count} times in the last 14 days the machine woke less than a minute after "
                      + $"entering sleep. The fastest was {Human(worst.Held)} on {worst.Slept:d MMM 'at' HH:mm}.",
                why: "This is a distinct fault from \"will not sleep\", and it is the one people misdiagnose for "
                     + "months. The machine does suspend, then something immediately pulls it back: usually a "
                     + "device armed to wake, an audio or network driver, or a USB device drawing attention. "
                     + "Because it happens in a bag, it looks like the laptop simply ran hot and flat.",
                action: "Work down this order: check the armed wake devices listed in this section, unplug USB "
                        + "peripherals and test again, then look at the wake source in the evidence below. If "
                        + "the source is \"Unknown\", the trigger is below Windows and is usually firmware or a "
                        + "device driver rather than an app.",
                evidence: timeline.ToString().TrimEnd());
        }
        else if (bounces.Count == 1)
        {
            m.Add(Severity.Advisory, "power.sleep-bounce", "The machine woke immediately after sleeping once",
                what: $"On {bounces[0].Slept:d MMM 'at' HH:mm} it woke after only {Human(bounces[0].Held)}.",
                why: "A single occurrence is usually someone opening the lid or nudging the mouse. A pattern of "
                     + "them means something is pulling the machine out of sleep.",
                action: "Nothing to do yet. If it becomes a habit, check the armed wake devices above.",
                evidence: timeline.ToString().TrimEnd());
        }
        else if (realWakes > 0)
        {
            m.Ok("power.sleep-bounce", "Sleep is holding",
                $"{realWakes} wake events in the last 14 days, none of them within a minute of going to sleep.",
                timeline.ToString().TrimEnd());
        }

        if (realWakes >= 3 && unknownSources == realWakes)
        {
            m.Add(Severity.Advisory, "power.wake-unknown", "Windows cannot tell what is waking this machine",
                what: $"All {unknownSources} recent wake events report the wake source as \"Unknown\".",
                why: "Windows records \"Unknown\" when the wake came from below the operating system — firmware, "
                     + "the embedded controller, or a device that woke the machine without reporting itself. "
                     + "It is a dead end in the event log, which is why chasing it through Task Scheduler never "
                     + "finds anything.",
                action: "Test by elimination rather than by log: disarm wake devices one at a time, and unplug "
                        + "USB peripherals and docks before closing the lid. A firmware update from the vendor "
                        + "is the other common fix.",
                evidence: timeline.ToString().TrimEnd());
        }
    }

    /// <summary>
    /// True for the pseudo-wake Windows logs when it moves from sleep down into
    /// hibernate. The machine did not come back; it went further under.
    /// </summary>
    private static bool IsDozeTransition(string source) =>
        source.Contains("Doze to Hibernate", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Hibernate from Sleep", StringComparison.OrdinalIgnoreCase);

    /// <summary>The event log pads timestamps with left-to-right marks that break DateTime parsing.</summary>
    private static string? Clean(string? s) =>
        s is null ? null : s.Replace("‎", "").Replace("‏", "").Trim();

    private static DateTime? ParseEventTime(string? raw)
    {
        var cleaned = Clean(raw);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return DateTime.TryParse(cleaned, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : null;
    }

    private static string Human(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:0.#} seconds"
        : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} minutes"
        : $"{t.TotalHours:0.#} hours";
}
