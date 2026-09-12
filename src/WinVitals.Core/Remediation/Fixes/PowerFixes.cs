using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

/// <summary>Puts automatic sleep back to sensible timeouts.</summary>
public sealed class RestoreSleepTimeouts : IFix
{
    private const int AcMinutes = 30;
    private const int DcMinutes = 15;

    public string FindingId => "power.sleep-never";
    public string Title => "Turn automatic sleep back on";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => false;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        $"Sets the machine to sleep after {AcMinutes} minutes idle when plugged in, and {DcMinutes} minutes "
        + "on battery. Nothing else changes, and the previous values are recorded so this can be put straight back.";

    public string Preview(Finding finding)
    {
        var current = PowerSettings.Read("SUB_SLEEP", "STANDBYIDLE");
        return $"Currently:  plugged in {PowerSettings.Timeout(current.Ac)}, on battery {PowerSettings.Timeout(current.Dc)}\n"
             + $"Will become: plugged in {AcMinutes} min, on battery {DcMinutes} min\n\n"
             + $"powercfg /change standby-timeout-ac {AcMinutes}\n"
             + $"powercfg /change standby-timeout-dc {DcMinutes}";
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        // Read the old values first. Undo information that is captured after the change
        // is not undo information.
        var before = PowerSettings.Read("SUB_SLEEP", "STANDBYIDLE");
        if (ctx.DryRun) return FixResult.Ok("Would set sleep timeouts to 30 / 15 minutes.");

        var undo = new List<UndoStep>();
        if (before.Ac >= 0)
            undo.Add(FixHelpers.Process($"Set plugged-in sleep back to {PowerSettings.Timeout(before.Ac)}",
                "powercfg.exe", $"/change standby-timeout-ac {before.Ac / 60}"));
        if (before.Dc >= 0)
            undo.Add(FixHelpers.Process($"Set on-battery sleep back to {PowerSettings.Timeout(before.Dc)}",
                "powercfg.exe", $"/change standby-timeout-dc {before.Dc / 60}"));

        var ac = FixHelpers.Run("powercfg.exe", $"/change standby-timeout-ac {AcMinutes}");
        var dc = FixHelpers.Run("powercfg.exe", $"/change standby-timeout-dc {DcMinutes}");

        if (!ac.Ok || !dc.Ok)
            return new FixResult(false, $"powercfg refused the change: {ac.Output} {dc.Output}", undo);

        return FixResult.Ok($"Sleep is now set to {AcMinutes} minutes plugged in and {DcMinutes} on battery.", undo);
    }
}

/// <summary>Stops scheduled tasks pulling the machine out of sleep.</summary>
public sealed class DisableWakeTimers : IFix
{
    public string FindingId => "power.waketimers";
    public string Title => "Stop scheduled tasks waking the machine";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Turns off wake timers for the current power plan, so nothing can bring the machine out of sleep at a "
        + "scheduled time. Windows Update maintenance simply waits until the machine is awake anyway. Alarms "
        + "you have set in apps are not affected.";

    public string Preview(Finding finding)
    {
        var current = PowerSettings.Read("SUB_SLEEP", "RTCWAKE");
        return $"Currently: wake timers {Describe(current.Ac)} plugged in, {Describe(current.Dc)} on battery\n\n"
             + "powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0\n"
             + "powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0\n"
             + "powercfg /setactive SCHEME_CURRENT";
    }

    private static string Describe(int value) => value switch
    {
        0 => "disabled", 1 => "enabled", 2 => "important only", _ => "unknown"
    };

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        var before = PowerSettings.Read("SUB_SLEEP", "RTCWAKE");
        if (ctx.DryRun) return FixResult.Ok("Would disable wake timers on the current power plan.");

        var undo = new List<UndoStep>();
        if (before.Ac >= 0)
            undo.Add(FixHelpers.Process($"Restore plugged-in wake timers to {Describe(before.Ac)}",
                "powercfg.exe", $"/setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE {before.Ac}"));
        if (before.Dc >= 0)
            undo.Add(FixHelpers.Process($"Restore on-battery wake timers to {Describe(before.Dc)}",
                "powercfg.exe", $"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE {before.Dc}"));
        undo.Add(FixHelpers.Process("Re-apply the power plan", "powercfg.exe", "/setactive SCHEME_CURRENT"));

        var a = FixHelpers.Run("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0");
        var b = FixHelpers.Run("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 0");
        var c = FixHelpers.Run("powercfg.exe", "/setactive SCHEME_CURRENT");

        if (!a.Ok || !b.Ok || !c.Ok)
            return new FixResult(false, $"powercfg refused the change: {a.Output} {b.Output} {c.Output}", undo);

        return FixResult.Ok("Wake timers are now off for this power plan.", undo);
    }
}

/// <summary>
/// Stops the network adapter waking the machine.
///
/// Only network adapters are disarmed. Disarming everything would take the keyboard
/// and mouse with it, and a laptop you cannot wake by pressing a key is a worse
/// problem than the one being fixed.
/// </summary>
public sealed class DisarmNetworkWake : IFix
{
    public string FindingId => "power.wake-armed";
    public string Title => "Stop the network adapter waking the machine";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Removes permission for the network adapter to wake this machine. Broadcast traffic on an office, "
        + "hotel or cafe network can otherwise wake a laptop repeatedly, including inside a bag. Your keyboard "
        + "and mouse keep their ability to wake it. This does switch off Wake-on-LAN, so skip it if you "
        + "deliberately wake this machine remotely.";

    /// <summary>Pulls the network adapters out of the finding's captured device list.</summary>
    internal static List<string> NetworkDevices(Finding finding)
    {
        var evidence = finding.Evidence ?? "";
        return evidence.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .Where(l =>
                l.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("GbE", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    public string Preview(Finding finding)
    {
        var devices = NetworkDevices(finding);
        if (devices.Count == 0) return "No network adapter is currently armed to wake this machine.";
        return string.Join("\n", devices.Select(d => $"powercfg /devicedisablewake \"{d}\""));
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        var devices = NetworkDevices(finding);
        if (devices.Count == 0)
            return FixResult.Failed("No network adapter is armed to wake this machine, so there is nothing to change.");

        if (ctx.DryRun) return FixResult.Ok($"Would disarm {devices.Count} network adapter(s).");

        var undo = new List<UndoStep>();
        var done = new List<string>();
        var failed = new List<string>();

        foreach (var device in devices)
        {
            // Record the reversal before attempting the change.
            undo.Add(FixHelpers.Process($"Let \"{device}\" wake the machine again",
                "powercfg.exe", $"/deviceenablewake \"{device}\""));

            var res = FixHelpers.Run("powercfg.exe", $"/devicedisablewake \"{device}\"");
            if (res.Ok) done.Add(device); else failed.Add($"{device}: {res.Output}");
        }

        if (done.Count == 0)
            return new FixResult(false, "None of the adapters could be disarmed: " + string.Join("; ", failed), undo);

        var message = $"Disarmed {done.Count} adapter(s): {string.Join(", ", done)}.";
        if (failed.Count > 0) message += $" Could not change: {string.Join("; ", failed)}.";
        return new FixResult(true, message, undo);
    }
}

/// <summary>Turns hibernate on so a flat battery does not lose open work.</summary>
public sealed class EnableHibernate : IFix
{
    public string FindingId => "power.no-hibernate";
    public string Title => "Turn hibernate on";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Lets the machine save memory to disk and power off completely, so running out of battery while asleep "
        + "does not lose whatever was open. It reserves disk space roughly proportional to your installed memory.";

    public string Preview(Finding finding) => "powercfg /hibernate on";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would enable hibernate.");

        var undo = new List<UndoStep>
        {
            FixHelpers.Process("Turn hibernate back off", "powercfg.exe", "/hibernate off"),
        };

        var res = FixHelpers.Run("powercfg.exe", "/hibernate on");
        return res.Ok
            ? FixResult.Ok("Hibernate is now available.", undo)
            : new FixResult(false, $"powercfg refused: {res.Output}", undo);
    }
}
