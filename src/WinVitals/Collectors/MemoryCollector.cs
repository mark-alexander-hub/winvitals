using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Installed memory, how it is arranged across slots, and whether the machine is
/// currently short of it.
///
/// The slot arrangement is the interesting part: a laptop running one stick in a
/// two-slot board loses a serious amount of graphics and general throughput to
/// single-channel operation, and nothing in Windows ever mentions it.
/// </summary>
public sealed class MemoryCollector : ICollector
{
    public string Id => "memory";
    public string Name => "Memory";
    public string Blurb => "How much memory is installed, how it is arranged, and whether it is under pressure.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        var os = Wmi.First("SELECT * FROM Win32_OperatingSystem");
        var sticks = Wmi.Query("SELECT * FROM Win32_PhysicalMemory");
        var array = Wmi.First("SELECT * FROM Win32_PhysicalMemoryArray");

        var totalBytes = sticks.Sum(s => s.Num("Capacity"));
        if (totalBytes == 0)
            totalBytes = (Wmi.First("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")?.Num("TotalPhysicalMemory")) ?? 0;

        m.Fact("Installed memory", CollectorExtensions.Bytes(totalBytes));

        var slots = array?.Num("MemoryDevices") ?? 0;
        if (slots > 0) m.Fact("Memory slots", $"{sticks.Count} of {slots} in use");

        foreach (var s in sticks)
        {
            var where = s.Str("DeviceLocator");
            var speed = s.Num("ConfiguredClockSpeed");
            if (speed == 0) speed = s.Num("Speed");
            var part = s.Str("PartNumber").Trim();

            m.Fact($"  {where}",
                $"{CollectorExtensions.Bytes(s.Num("Capacity"))}"
                + (speed > 0 ? $" @ {speed} MT/s" : "")
                + (part.Length > 0 ? $" — {part}" : ""));
        }

        var evidence = string.Join("\n", sticks.Select(s =>
            $"{s.Str("DeviceLocator"),-16} {CollectorExtensions.Bytes(s.Num("Capacity")),8}  "
            + $"{(s.Num("ConfiguredClockSpeed") is var c and > 0 ? c : s.Num("Speed"))} MT/s  {s.Str("PartNumber").Trim()}"));

        // --- Single channel ------------------------------------------------
        if (sticks.Count == 1 && slots >= 2)
        {
            m.Add(Severity.Advisory, "memory.single-channel", "Only one memory slot is populated",
                what: $"One {CollectorExtensions.Bytes(sticks[0].Num("Capacity"))} module is installed in a machine with {slots} slots.",
                why: "With one stick the memory runs in single-channel mode, roughly halving memory bandwidth. "
                     + "On a laptop using integrated graphics that is a large, measurable loss, because the GPU "
                     + "shares this same memory. It is the most commonly missed cause of a machine that feels "
                     + "slower than its specification suggests.",
                action: "Adding a second module of the same capacity and speed enables dual-channel. Match the "
                        + "existing part number where you can.",
                evidence: evidence);
        }
        else if (sticks.Count > 1)
        {
            var capacities = sticks.Select(s => s.Num("Capacity")).Distinct().Count();
            var speeds = sticks.Select(s => s.Num("ConfiguredClockSpeed") is var c and > 0 ? c : s.Num("Speed"))
                               .Distinct().Count();

            if (capacities > 1 || speeds > 1)
            {
                m.Add(Severity.Advisory, "memory.mismatched", "The memory modules are not identical",
                    what: "The installed modules differ in capacity or speed.",
                    why: "Mixed modules still work, but the machine falls back to the slowest common speed, and "
                         + "mismatched capacities run only part of the memory in dual-channel.",
                    action: "Nothing needs fixing. If you are buying more memory, matching the existing modules "
                            + "gets the best out of them.",
                    evidence: evidence);
            }
            else
            {
                m.Ok("memory.channels", "Memory is installed in matched pairs",
                    $"{sticks.Count} identical modules, so the machine runs in dual-channel.", evidence);
            }
        }

        // --- Total ---------------------------------------------------------
        var totalGb = totalBytes / 1024.0 / 1024 / 1024;
        if (totalGb > 0 && totalGb < 7.5)
        {
            m.Add(Severity.Warning, "memory.small", $"{CollectorExtensions.Bytes(totalBytes)} of memory is tight for this version of Windows",
                what: $"This machine has {CollectorExtensions.Bytes(totalBytes)} installed.",
                why: "Windows 11 with a browser and one office application routinely uses more than this, so the "
                     + "machine spends its time moving memory to disk. That shows up as everything being slow at "
                     + "once, especially when switching between windows.",
                action: "More memory is the highest-value upgrade on a machine like this, ahead of anything "
                        + "software can do.",
                evidence: evidence);
        }

        // --- Pressure right now ---------------------------------------------
        if (os is not null)
        {
            var freeKb = os.Num("FreePhysicalMemory");
            var totalKb = os.Num("TotalVisibleMemorySize");
            if (totalKb > 0)
            {
                var usedPct = (totalKb - freeKb) * 100.0 / totalKb;
                m.Fact("Memory in use right now", $"{usedPct:0}%",
                    $"{CollectorExtensions.Bytes(freeKb * 1024)} free");

                if (usedPct >= 90)
                {
                    m.Add(Severity.Advisory, "memory.pressure", "Memory is almost full right now",
                        what: $"{usedPct:0}% of physical memory is in use at the moment of the scan.",
                        why: "A single busy moment is not a fault. If it is like this most of the time, the "
                             + "machine is paging constantly and that is what makes it feel slow.",
                        action: "Open Task Manager, sort by Memory, and see what is at the top. If nothing "
                                + "obvious explains it, this machine wants more memory.",
                        command: "taskmgr",
                        evidence: $"Total visible : {CollectorExtensions.Bytes(totalKb * 1024)}\n"
                                  + $"Free          : {CollectorExtensions.Bytes(freeKb * 1024)}\n"
                                  + $"In use        : {usedPct:0.#}%");
                }
            }
        }

        // --- Page file -------------------------------------------------------
        var pf = Wmi.First("SELECT * FROM Win32_PageFileUsage");
        if (pf is not null)
        {
            var allocated = pf.Num("AllocatedBaseSize");
            var peak = pf.Num("PeakUsage");
            m.Fact("Page file", $"{allocated:N0} MB allocated", $"peak use {peak:N0} MB — {pf.Str("Name")}");

            if (allocated > 0 && peak > allocated * 0.9)
            {
                m.Add(Severity.Advisory, "memory.pagefile-peak", "The page file has been nearly exhausted",
                    what: $"Peak page file use reached {peak:N0} MB of {allocated:N0} MB allocated.",
                    why: "When the page file fills, applications start failing to allocate memory and Windows "
                         + "shows 'out of memory' errors even though the disk has space.",
                    action: "Let Windows manage the page file size, or add physical memory.",
                    evidence: $"{pf.Str("Name")}: allocated {allocated} MB, peak {peak} MB");
            }
        }
    }
}
