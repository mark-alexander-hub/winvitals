using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Devices Windows is unhappy about — the yellow triangles in Device Manager,
/// translated out of error-code numbers into what actually went wrong.
/// </summary>
public sealed class DeviceCollector : ICollector
{
    public string Id => "devices";
    public string Name => "Devices & Drivers";
    public string Blurb => "Hardware that Windows cannot start, has no driver for, or has disabled.";

    /// <summary>
    /// Device Manager's ConfigManagerErrorCode. Only the ones a person can act on are
    /// spelled out; the rest fall through to a generic message with the code.
    /// </summary>
    private static readonly Dictionary<long, (string Short, string Meaning, string Fix)> Codes = new()
    {
        [1] = ("not configured correctly", "Windows has no working configuration for this device.",
               "Install the driver from the hardware vendor's site."),
        [3] = ("driver may be corrupted", "The driver loaded but the system is short of resources or the driver is damaged.",
               "Reinstall the driver."),
        [10] = ("cannot start", "The driver loaded but the device refused to start. Usually a firmware, power or driver-version problem.",
                "Reinstall the driver from the vendor. If it persists, check for a BIOS update."),
        [12] = ("not enough resources", "The device needs an interrupt or memory range that another device already holds.",
                "Disable a device you do not need, or check for a BIOS update."),
        [14] = ("needs a restart", "The device will not work correctly until the machine restarts.",
                "Restart the machine."),
        [18] = ("drivers need reinstalling", "Windows wants the driver reinstalled for this device.",
                "Reinstall the driver."),
        [19] = ("registry entry is damaged", "The configuration information for this device is corrupt.",
                "Uninstall the device in Device Manager and scan for hardware changes."),
        [21] = ("being removed", "Windows is in the middle of removing this device.",
                "Restart the machine; this usually clears itself."),
        [22] = ("disabled", "Somebody disabled this device, in Device Manager or in the BIOS.",
                "Enable it in Device Manager, or in BIOS setup if it is not listed there."),
        [24] = ("not present", "The device is registered but not physically there, or it has failed.",
                "Reseat the hardware if it is internal. If it is gone for good, remove it in Device Manager."),
        [28] = ("no driver installed", "Windows has no driver for this device at all.",
                "Install the driver from the hardware vendor's support page for this exact model."),
        [31] = ("cannot load its driver", "Windows cannot load the drivers this device needs.",
                "Reinstall the driver."),
        [32] = ("start type is disabled", "The driver's start type is set to disabled in the registry.",
                "Reinstall the driver, or re-enable its service."),
        [37] = ("driver failed to initialise", "The driver returned a failure when Windows initialised it.",
                "Reinstall the driver."),
        [39] = ("driver is missing or corrupt", "Windows cannot load the driver file for this device.",
                "Reinstall the driver."),
        [43] = ("stopped by Windows", "Windows stopped this device because it reported a problem. Very common for USB and graphics hardware.",
                "Unplug and reconnect it if external. Otherwise reinstall the driver, then suspect the hardware."),
        [45] = ("not currently connected", "This device is not attached right now. Windows remembers it from last time.",
                "Nothing to do unless you expected it to be plugged in."),
        [47] = ("prepared for removal", "The device was ejected and is waiting to be unplugged.",
                "Unplug and reconnect it."),
        [52] = ("driver is not signed", "Windows cannot verify the digital signature on this driver.",
                "Get a signed driver from the vendor. Do not disable signature enforcement to work around this."),
    };

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        var problems = Wmi.Query(
            "SELECT Name, DeviceID, ConfigManagerErrorCode, Status FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");

        // Code 45 means "was here last time, not plugged in now". Every machine has a
        // pile of these and none of them are faults. Counting them as problems is why
        // so many "PC health" tools report scary numbers that mean nothing.
        var real = problems.Where(p => p.Num("ConfigManagerErrorCode") != 45).ToList();
        var absent = problems.Count - real.Count;

        m.Fact("Devices present", Wmi.Query("SELECT DeviceID FROM Win32_PnPEntity").Count.ToString());
        if (absent > 0)
            m.Fact("Remembered but unplugged", absent.ToString(), "normal — not faults");

        if (real.Count == 0)
        {
            m.Ok("devices.problems", "No device is reporting a problem",
                "Nothing in Device Manager has a warning against it.",
                absent > 0 ? $"{absent} device(s) are simply not connected right now (code 45)." : null);
            return;
        }

        foreach (var d in real)
        {
            var name = d.Str("Name");
            var code = d.Num("ConfigManagerErrorCode");
            var known = Codes.TryGetValue(code, out var info);

            var severity = code switch
            {
                22 => Severity.Advisory,       // deliberately disabled
                24 or 47 => Severity.Advisory, // transient / removal states
                14 => Severity.Advisory,       // just needs a restart
                _ => Severity.Warning,
            };

            m.Add(severity, $"devices.code{code}.{Slug(name)}",
                $"{name} — {(known ? info.Short : $"error code {code}")}",
                what: known ? info.Meaning : $"Windows reports Device Manager error code {code} for this device.",
                why: "A device in this state is either not working at all or working in a reduced mode. If it "
                     + "is something you rely on — Wi-Fi, audio, the touchpad — this is your explanation.",
                action: known ? info.Fix : "Look up Device Manager error code " + code + " for this device type.",
                command: "devmgmt.msc",
                evidence: $"Name        : {name}\nDevice ID   : {d.Str("DeviceID")}\n"
                          + $"Error code  : {code}\nStatus      : {d.Str("Status")}");
        }
    }

    private static string Slug(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
