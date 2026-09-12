using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// How long this PC takes to start, from Windows' own measurements.
///
/// A slow boot is the symptom people mean when they say an old PC is slow, and it
/// is also the one thing here with a number attached, so a change can be shown to
/// have worked.
/// </summary>
public sealed class BootCollector : ICollector
{
    public string Id => "boot";
    public string Name => "Startup speed";
    public string Blurb => "How long this PC takes to boot, which part of that you can change, and whether it is getting slower.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        var boots = BootHistory.Read(20);

        if (boots.Count == 0)
        {
            m.Add(Severity.Unknown, "boot.none", "No boot timing has been recorded",
                "Windows has no boot duration records to read.",
                why: "The Diagnostics-Performance log is usually readable only by administrators, and a "
                     + "machine that is only ever put to sleep or uses Fast Startup may not log a full boot "
                     + "for weeks.",
                action: "Restart the machine (not shut down) and run this check again after it has started.",
                needsElevation: !ctx.Elevated);
            return;
        }

        var last = boots[0];
        m.Fact("Last boot", BootHistory.Seconds(last.TotalMs), $"{last.When:ddd d MMM, HH:mm}");
        if (last.PostBootMs > 0)
            m.Fact("  of which startup programs", BootHistory.Seconds(last.PostBootMs),
                "the part you can change");

        var recent = boots.Take(5).ToList();
        var average = (int)recent.Average(b => b.TotalMs);
        m.Fact("Average of last " + recent.Count, BootHistory.Seconds(average));
        m.Fact("Fastest recorded", BootHistory.Seconds(boots.Min(b => b.TotalMs)));

        var evidence = string.Join("\n", boots.Take(10).Select(b =>
            $"{b.When:yyyy-MM-dd HH:mm}  total {b.TotalMs / 1000.0,6:0.0}s   to desktop {b.MainPathMs / 1000.0,5:0.0}s   after {b.PostBootMs / 1000.0,5:0.0}s"));

        // --- How slow -------------------------------------------------------
        var seconds = last.TotalMs / 1000.0;
        if (seconds >= 120)
        {
            m.Add(Severity.Warning, "boot.slow", $"This PC takes {seconds:0} seconds to start",
                what: $"The last boot took {seconds:0} seconds. {StartupShare(last)}",
                why: "Anything over about a minute on a modern build is startup programs, a mechanical "
                     + "disk, or both. This is the single most visible way an old PC feels old.",
                action: "Turn off the startup programs you do not need on the Speed up page, then restart "
                        + "and compare. If the disk is mechanical, that is the other half of the answer.",
                evidence: evidence);
        }
        else if (seconds >= 60)
        {
            m.Add(Severity.Advisory, "boot.slowish", $"Startup takes {seconds:0} seconds",
                what: $"The last boot took {seconds:0} seconds. {StartupShare(last)}",
                why: "Not bad, but the time after the desktop appears is almost entirely startup programs, "
                     + "and that part is yours to trim.",
                action: "See what launches at sign-in on the Speed up page.",
                evidence: evidence);
        }
        else
        {
            m.Ok("boot.fast", $"Starts in {seconds:0} seconds",
                $"The last boot took {seconds:0} seconds, which is quick.", evidence);
        }

        // --- Getting worse? ---------------------------------------------------
        if (boots.Count >= 6)
        {
            var newer = boots.Take(3).Average(b => b.TotalMs);
            var older = boots.Skip(3).Take(3).Average(b => b.TotalMs);
            if (older > 0 && newer > older * 1.3 && newer - older > 10_000)
            {
                m.Add(Severity.Advisory, "boot.trend", "Startup is getting slower",
                    what: $"The last three boots averaged {newer / 1000:0} s; the three before that averaged {older / 1000:0} s.",
                    why: "Boot time creeping up usually means something new has started launching at sign-in "
                         + "— an updater, a cloud client, a game launcher — rather than the hardware slowing.",
                    action: "Check the Speed up page for recently added startup programs.",
                    evidence: evidence);
            }
        }
    }

    private static string StartupShare(BootRecord b)
    {
        if (b.PostBootMs <= 0 || b.TotalMs <= 0) return "";
        var pct = b.PostBootMs * 100 / b.TotalMs;
        return $"About {b.PostBootMs / 1000.0:0} seconds of that ({pct}%) is startup programs loading after the desktop appears.";
    }
}
