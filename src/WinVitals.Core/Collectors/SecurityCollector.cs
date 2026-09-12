using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// The defences that are supposed to be on: antivirus, firewall, disk encryption,
/// and whether this machine is quietly sharing anything with the network.
/// </summary>
public sealed class SecurityCollector : ICollector
{
    public string Id => "security";
    public string Name => "Security";
    public string Blurb => "Antivirus, firewall, disk encryption, and what this machine exposes to the network.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        Antivirus(m);
        Firewall(m);
        Encryption(ctx, m);
        Shares(m);
        RemoteDesktop(m);
    }

    // ------------------------------------------------------------ antivirus

    private static void Antivirus(ModuleResult m)
    {
        // Ask Security Center first. If a third-party product is installed, Defender
        // deliberately stands down, and reporting "Defender is off" would be alarming
        // and wrong.
        var products = Wmi.Query("SELECT * FROM AntiVirusProduct", @"root\SecurityCenter2");
        var names = new List<string>();
        var anyEnabled = false;
        var anyOutOfDate = false;

        foreach (var p in products)
        {
            var name = p.Str("displayName");
            var state = p.Num("productState");
            var hex = state.ToString("X6");

            var enabled = hex.Length >= 4 && (hex.Substring(2, 2) is "10" or "11");
            var upToDate = hex.Length >= 6 && hex.Substring(4, 2) == "00";

            names.Add($"{name} ({(enabled ? "on" : "off")}, definitions {(upToDate ? "current" : "out of date")})");
            if (enabled) anyEnabled = true;
            if (enabled && !upToDate) anyOutOfDate = true;
        }

        if (names.Count > 0) m.Fact("Antivirus", string.Join("; ", names));

        var defender = Wmi.First("SELECT * FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender");
        var sigAge = defender?.Num("AntivirusSignatureAge") ?? -1;
        var realtime = defender?.Flag("RealTimeProtectionEnabled") ?? false;

        var evidence = string.Join("\n", names.Prepend("Security Center products:"))
                       + (defender is not null
                           ? $"\n\nDefender:\n  RealTimeProtectionEnabled = {realtime}"
                             + $"\n  AntivirusSignatureAge     = {sigAge} day(s)"
                           : "\n\nDefender status unavailable.");

        if (!anyEnabled && products.Count > 0)
        {
            m.Add(Severity.Critical, "security.av-off", "No antivirus is currently running",
                what: "Security Center lists " + string.Join(", ", names) + ", none of them active.",
                why: "With real-time protection off, nothing is inspecting files as they are written or run.",
                action: "Turn your antivirus back on. If a product was uninstalled badly, Windows Security "
                        + "will take over once the stale registration is cleared.",
                command: "start windowsdefender:",
                evidence: evidence);
        }
        else if (anyOutOfDate || sigAge > 7)
        {
            m.Add(Severity.Warning, "security.av-stale", "Antivirus definitions are out of date",
                what: sigAge > 0
                    ? $"The last definition update was {sigAge} day(s) ago."
                    : "Security Center reports the definitions as out of date.",
                why: "Definitions age quickly. A scanner a week behind will miss most of what is currently "
                     + "circulating.",
                action: "Update definitions now, and check why automatic updating stopped.",
                command: "\"%ProgramFiles%\\Windows Defender\\MpCmdRun.exe\" -SignatureUpdate",
                evidence: evidence);
        }
        else if (anyEnabled || realtime)
        {
            m.Ok("security.av", "Antivirus is on and current",
                names.Count > 0 ? string.Join("; ", names) : "Microsoft Defender real-time protection is enabled.",
                evidence);
        }
        else
        {
            m.Add(Severity.Unknown, "security.av", "Antivirus status could not be read",
                "Neither Security Center nor Defender returned a usable status.",
                why: "Common on machines managed by a company, where these interfaces are restricted.",
                evidence: evidence);
        }
    }

    // ------------------------------------------------------------- firewall

    private static void Firewall(ModuleResult m)
    {
        // Read the registry rather than parsing netsh: netsh output is localised, and
        // a scan should not depend on the machine being in English.
        var profiles = new[] { "DomainProfile", "StandardProfile", "PublicProfile" };
        var friendly = new Dictionary<string, string>
        {
            ["DomainProfile"] = "Domain (work network)",
            ["StandardProfile"] = "Private (home network)",
            ["PublicProfile"] = "Public (untrusted network)",
        };

        var off = new List<string>();
        var evidence = new List<string>();

        foreach (var profile in profiles)
        {
            var value = ReadDword($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}", "EnableFirewall");

            // Absent means default, and the default is on.
            var enabled = value is null || value == 1;
            evidence.Add($"{friendly[profile],-28} {(enabled ? "on" : "OFF")}");
            if (!enabled) off.Add(friendly[profile]);
        }

        m.Fact("Firewall", off.Count == 0 ? "on for all profiles" : "off for " + string.Join(", ", off));

        if (off.Count > 0)
        {
            m.Add(Severity.Critical, "security.firewall", "The Windows firewall is switched off",
                what: "It is off for: " + string.Join(", ", off) + ".",
                why: "With the firewall off, anything on the same network — a cafe, hotel, airport or office "
                     + "network — can reach services on this machine directly.",
                action: "Turn it back on for every profile. If a third-party firewall is managing this, verify "
                        + "that it is actually running.",
                command: "netsh advfirewall set allprofiles state on",
                undo: "netsh advfirewall set allprofiles state off",
                evidence: string.Join("\n", evidence));
        }
        else
        {
            m.Ok("security.firewall", "Firewall is on for all profiles",
                "Domain, private and public profiles are all protected.", string.Join("\n", evidence));
        }
    }

    // ------------------------------------------------------------ encryption

    private static void Encryption(ScanContext ctx, ModuleResult m)
    {
        if (!ctx.Elevated)
        {
            m.NeedsAdmin("security.bitlocker", "Cannot check whether the disk is encrypted",
                "Reading BitLocker status needs administrator rights.");
            return;
        }

        var volumes = Wmi.Query("SELECT * FROM Win32_EncryptableVolume",
            @"root\CIMV2\Security\MicrosoftVolumeEncryption");

        if (volumes.Count == 0)
        {
            m.Add(Severity.Unknown, "security.bitlocker", "BitLocker is not available on this edition",
                "No encryptable volumes were reported.",
                why: "Windows Home does not include BitLocker management, though it may still have Device "
                     + "Encryption enabled if the hardware supports it.",
                action: "Check Settings > Privacy & security > Device encryption.",
                command: "start ms-settings:deviceencryption");
            return;
        }

        var system = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var evidence = new List<string>();
        var unprotected = new List<string>();

        foreach (var v in volumes)
        {
            var letter = v.Str("DriveLetter");
            var status = v.Num("ProtectionStatus"); // 0 off, 1 on, 2 unknown
            var text = status switch { 1 => "protected", 0 => "not protected", _ => "unknown" };
            evidence.Add($"{letter,-4} {text}");
            if (status == 0 && !string.IsNullOrEmpty(letter)) unprotected.Add(letter);
        }

        m.Fact("Disk encryption", unprotected.Count == 0 ? "all volumes protected" : "not on " + string.Join(", ", unprotected));

        if (unprotected.Any(u => u.Equals(system, StringComparison.OrdinalIgnoreCase)))
        {
            m.Add(Severity.Warning, "security.bitlocker", "The system drive is not encrypted",
                what: $"Drive {system} has no BitLocker protection.",
                why: "Anyone who takes this laptop can read everything on it by moving the disk to another "
                     + "machine. On a portable device that is the realistic threat, far more than malware.",
                action: "Turn on Device Encryption or BitLocker. Save the recovery key somewhere that is not "
                        + "this laptop — without it, an encrypted disk is unrecoverable.",
                command: "start ms-settings:deviceencryption",
                evidence: string.Join("\n", evidence));
        }
        else
        {
            m.Ok("security.bitlocker", "The system drive is encrypted",
                $"Drive {system} is protected by BitLocker.", string.Join("\n", evidence));
        }
    }

    // ---------------------------------------------------------------- shares

    private static void Shares(ModuleResult m)
    {
        var shares = Wmi.Query("SELECT * FROM Win32_Share");

        // Type 0 is a disk share. The built-in administrative shares (C$, ADMIN$, IPC$)
        // end in $ and are a different matter: they need credentials and are expected.
        var exposed = shares
            .Where(s => s.Num("Type") == 0 && !s.Str("Name").EndsWith("$"))
            .Select(s => (Name: s.Str("Name"), Path: s.Str("Path")))
            .ToList();

        if (exposed.Count == 0)
        {
            m.Ok("security.shares", "No folders are shared on the network",
                "Only the default administrative shares exist.",
                string.Join("\n", shares.Select(s => $"{s.Str("Name"),-16} {s.Str("Path")}")));
            return;
        }

        m.Fact("Shared folders", string.Join(", ", exposed.Select(e => e.Name)));

        // A share rooted at a user profile, a drive root, or Windows itself hands the
        // whole tree to the network. This is the shape of an accidental share.
        var broad = exposed.Where(e =>
            e.Path.TrimEnd('\\').Length <= 3 ||
            e.Path.Contains(@"\Users", StringComparison.OrdinalIgnoreCase) ||
            e.Path.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase)).ToList();

        var severity = broad.Count > 0 ? Severity.Warning : Severity.Advisory;

        m.Add(severity, "security.shares", $"{exposed.Count} folder(s) are shared on the network",
            what: string.Join("; ", exposed.Select(e => $"\"{e.Name}\" shares {e.Path}"))
                  + (broad.Count > 0 ? " — at least one of these shares an entire drive or user profile." : ""),
            why: broad.Count > 0
                ? "Sharing a drive root or a user profile exposes documents, downloads and saved data to "
                  + "everyone who can reach this machine, which on a public or office network is more people "
                  + "than you would choose. Shares set up once for a file transfer are routinely forgotten."
                : "Anyone on the same network can reach these folders, subject to their permissions.",
            action: "Remove any share you did not deliberately set up. The command lists them all so you can "
                    + "check before deleting anything.",
            command: "net share\n:: then, for one you do not want:\n:: net share <name> /delete",
            evidence: string.Join("\n", exposed.Select(e => $"{e.Name,-16} {e.Path}")));
    }

    private static void RemoteDesktop(ModuleResult m)
    {
        var deny = ReadDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
        var enabled = deny == 0;

        m.Fact("Remote Desktop", enabled ? "enabled" : "disabled");

        if (!enabled)
        {
            m.Ok("security.rdp", "Remote Desktop is off", "Nothing can connect to this machine's desktop remotely.");
            return;
        }

        var nla = ReadDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication");

        m.Add(nla == 1 ? Severity.Advisory : Severity.Warning, "security.rdp",
            "Remote Desktop is enabled" + (nla == 1 ? "" : " without Network Level Authentication"),
            what: "This machine accepts Remote Desktop connections"
                  + (nla == 1 ? " with Network Level Authentication required." : ", and NLA is not required."),
            why: nla == 1
                ? "That is a reasonable configuration, but it does mean the machine is listening for logins on "
                  + "any network it joins. Exposed RDP is one of the most-scanned services on the internet."
                : "Without NLA, an unauthenticated stranger can reach the login screen and the code behind it. "
                  + "This is the configuration used in most RDP compromises.",
            action: nla == 1
                ? "Fine if you use it. Turn it off if you do not."
                : "Require Network Level Authentication, and turn Remote Desktop off entirely if you do not use it.",
            command: nla == 1
                ? "start ms-settings:remotedesktop"
                : "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp\" /v UserAuthentication /t REG_DWORD /d 1 /f",
            evidence: $"fDenyTSConnections = {deny?.ToString() ?? "not set"}\nUserAuthentication (NLA) = {nla?.ToString() ?? "not set"}");
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            var value = key?.GetValue(name);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }
}
