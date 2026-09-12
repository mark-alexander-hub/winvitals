using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Identity of the machine: what it is, what firmware it runs, how long it has been up.
///
/// Note what this module deliberately does NOT do: it never says a BIOS or driver is
/// "out of date". Offline guesses about what version is current are wrong often enough
/// to send people hunting for updates that do not exist. It reports what is installed
/// and links the vendor page that holds the real answer.
/// </summary>
public sealed class SystemCollector : ICollector
{
    public string Id => "system";
    public string Name => "System & Firmware";
    public string Blurb => "What this machine is, what firmware it runs, and how long it has been running.";

    private static readonly Dictionary<string, string> VendorSupport = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LENOVO"] = "https://pcsupport.lenovo.com/downloads",
        ["Dell Inc."] = "https://www.dell.com/support/home",
        ["HP"] = "https://support.hp.com/drivers",
        ["Hewlett-Packard"] = "https://support.hp.com/drivers",
        ["ASUSTeK COMPUTER INC."] = "https://www.asus.com/support/",
        ["Acer"] = "https://www.acer.com/support",
        ["Micro-Star International Co., Ltd."] = "https://www.msi.com/support",
        ["Microsoft Corporation"] = "https://support.microsoft.com/surface-updates",
        ["Framework"] = "https://knowledgebase.frame.work/bios-guides",
        ["Samsung Electronics Co., Ltd."] = "https://www.samsung.com/us/support/downloads/",
        ["TOSHIBA"] = "https://support.dynabook.com/",
        ["Apple Inc."] = "https://support.apple.com/boot-camp",
    };

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        var cs = Wmi.First("SELECT * FROM Win32_ComputerSystem");
        var bios = Wmi.First("SELECT * FROM Win32_BIOS");
        var os = Wmi.First("SELECT * FROM Win32_OperatingSystem");
        var cpu = Wmi.First("SELECT * FROM Win32_Processor");

        var manufacturer = cs?.Str("Manufacturer") ?? "";
        var model = cs?.Str("Model") ?? "";
        var family = cs?.Str("SystemFamily") ?? "";

        m.Fact("Manufacturer", manufacturer);
        m.Fact("Model", string.IsNullOrWhiteSpace(family) || family == model ? model : $"{model} ({family})");
        m.Fact("Type", cs?.Str("SystemType") ?? "");
        m.Fact("Processor", cpu?.Str("Name") ?? "");

        var serial = bios?.Str("SerialNumber") ?? "";
        if (!string.IsNullOrWhiteSpace(serial))
            m.Fact("Serial number", ctx.Redactor.Serial(serial));

        // --- Firmware ------------------------------------------------------
        var supportUrl = LookupSupport(manufacturer);
        var biosVersion = bios?.Str("SMBIOSBIOSVersion") ?? "";
        var biosDate = bios?.Date("ReleaseDate");

        if (!string.IsNullOrWhiteSpace(biosVersion))
        {
            var note = biosDate.HasValue ? $"released {biosDate:yyyy-MM-dd}" : null;
            m.Fact("BIOS / UEFI version", biosVersion, note, supportUrl);

            m.Add(Severity.Advisory, "system.firmware", "Check your firmware version against the vendor page",
                what: $"This machine reports BIOS/UEFI {biosVersion}"
                      + (biosDate.HasValue ? $", released {biosDate:d MMMM yyyy}." : "."),
                why: "Firmware updates fix power, thermal and security problems that no amount of Windows "
                     + "tinkering can. WinVitals will not guess whether yours is current: only the vendor's "
                     + "download page knows what the newest release actually is, and offline guesses send "
                     + "people chasing updates that do not exist.",
                action: supportUrl is null
                    ? "Look up this model on the manufacturer's support site and compare the newest BIOS to the version above."
                    : "Open the vendor page below, enter this model, and compare the newest BIOS release to the version above.",
                evidence: $"Manufacturer : {manufacturer}\nModel        : {model}\nBIOS version : {biosVersion}\n"
                          + $"BIOS date    : {(biosDate.HasValue ? biosDate.Value.ToString("yyyy-MM-dd") : "not reported")}",
                verifyAt: supportUrl);
        }

        m.Fact("Secure Boot", ReadSecureBoot());

        // --- Windows -------------------------------------------------------
        var caption = os?.Str("Caption").Replace("Microsoft ", "") ?? "";
        var build = os?.Str("BuildNumber") ?? "";
        var display = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var ubr = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        var fullBuild = string.IsNullOrEmpty(ubr) ? build : $"{build}.{ubr}";

        m.Fact("Windows", string.IsNullOrEmpty(display) ? caption : $"{caption}, version {display}");
        m.Fact("Build", fullBuild, verifyAt: "https://learn.microsoft.com/windows/release-health/");
        m.Fact("Architecture", os?.Str("OSArchitecture") ?? "");

        var installed = os?.Date("InstallDate");
        if (installed.HasValue)
            m.Fact("Windows installed", installed.Value.ToString("d MMMM yyyy"),
                $"{(int)(DateTime.Now - installed.Value).TotalDays} days ago");

        // --- Uptime --------------------------------------------------------
        var boot = os?.Date("LastBootUpTime");
        if (boot.HasValue)
        {
            var up = DateTime.Now - boot.Value;
            m.Fact("Last boot", boot.Value.ToString("ddd d MMM, HH:mm"), CollectorExtensions.Duration(up) + " ago");

            var evidence = $"Last boot : {boot:yyyy-MM-dd HH:mm:ss}\nUptime    : {up.TotalDays:0.0} days";

            // Fast Startup means "shut down" often is not a real boot, so uptime can be
            // misleading. Say so rather than scolding someone who shuts down nightly.
            var fastStartup = ReadRegistry(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
            var fastStartupOn = fastStartup == "1";

            if (up.TotalDays >= 14)
            {
                m.Add(Severity.Warning, "system.uptime", $"Running for {(int)up.TotalDays} days without a restart",
                    what: $"This machine last booted on {boot:d MMMM yyyy} and has been up for {(int)up.TotalDays} days.",
                    why: "Windows defers a lot of work until a restart: pending security patches, driver "
                         + "replacements and servicing cleanups. Long uptimes are also the usual reason a "
                         + "machine slowly gets sluggish and leaks memory.",
                    action: "Restart when convenient. If updates are pending, this is what is blocking them.",
                    evidence: evidence);
            }
            else if (up.TotalDays >= 7)
            {
                m.Add(Severity.Advisory, "system.uptime", $"Up for {(int)up.TotalDays} days",
                    what: $"Uptime is {(int)up.TotalDays} days.",
                    why: "Not a problem yet, but pending updates only finish installing after a restart.",
                    action: "A restart this week will let any deferred updates complete.",
                    evidence: evidence);
            }
            else
            {
                m.Ok("system.uptime", "Uptime is healthy",
                    $"Up for {CollectorExtensions.Duration(up)} since {boot:ddd d MMM HH:mm}.", evidence);
            }

            if (fastStartupOn)
            {
                m.Add(Severity.Advisory, "system.fast-startup", "Fast Startup is on, so \"Shut down\" is not a full restart",
                    what: "Fast Startup (hiberboot) is enabled.",
                    why: "With it on, choosing Shut down hibernates the kernel instead of ending the session. "
                         + "Driver and update problems that a real restart would clear can survive being "
                         + "\"switched off overnight\", which is why the fix so often is \"restart, do not shut down\".",
                    action: "Nothing is wrong with leaving it on. Just use Restart, not Shut down, when you are trying to clear a problem.",
                    command: "powercfg /hibernate off   :: also disables Fast Startup and frees the hibernation file",
                    undo: "powercfg /hibernate on",
                    evidence: "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power\\HiberbootEnabled = 1");
            }
        }
    }

    private static string? LookupSupport(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        if (VendorSupport.TryGetValue(manufacturer, out var exact)) return exact;
        return VendorSupport.FirstOrDefault(kv =>
            manufacturer.Contains(kv.Key.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string ReadSecureBoot()
    {
        // UEFISecureBootEnabled is absent entirely on legacy-BIOS installs.
        var v = ReadRegistry(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        return v switch { "1" => "On", "0" => "Off", _ => "Not reported (legacy BIOS or not supported)" };
    }

    private static string ReadRegistry(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
