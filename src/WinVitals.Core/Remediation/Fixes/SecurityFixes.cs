using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

internal static class Reg
{
    private const string FirewallRoot =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    public static string FirewallProfileKey(string profile) => $@"{FirewallRoot}\{profile}";

    /// <summary>Reads a DWORD as a string, or null when the value does not exist.</summary>
    public static string? ReadDwordAsString(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            var value = key?.GetValue(name);
            return value is null ? null : Convert.ToInt32(value).ToString();
        }
        catch
        {
            return null;
        }
    }

    public static bool WriteDword(RegistryKey hive, string path, string name, int value)
    {
        try
        {
            using var key = hive.CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Turns the Windows firewall back on for every profile.</summary>
public sealed class TurnOnFirewall : IFix
{
    private static readonly string[] Profiles = { "DomainProfile", "StandardProfile", "PublicProfile" };

    public string FindingId => "security.firewall";
    public string Title => "Turn the firewall back on";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Switches the Windows firewall on for home, work and public networks. If a program genuinely needs to "
        + "accept incoming connections, Windows will ask you the first time rather than silently blocking it.";

    public string Preview(Finding finding) => "netsh advfirewall set allprofiles state on";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would turn the firewall on for all profiles.");

        // Capture each profile separately: a machine can have the firewall off for one
        // profile only, and blanket-undoing to "off everywhere" would be a new fault.
        var undo = new List<UndoStep>();
        foreach (var profile in Profiles)
        {
            var path = Reg.FirewallProfileKey(profile);
            var previous = Reg.ReadDwordAsString(Registry.LocalMachine, path, "EnableFirewall");
            undo.Add(FixHelpers.Registry(
                $"Restore the {profile} firewall setting", "HKLM", path, "EnableFirewall", previous, "dword"));
        }

        var res = FixHelpers.Run("netsh.exe", "advfirewall set allprofiles state on");
        return res.Ok
            ? FixResult.Ok("The firewall is on for all three profiles.", undo)
            : new FixResult(false, $"netsh refused: {res.Output}", undo);
    }
}

/// <summary>Switches Remote Desktop off.</summary>
public sealed class DisableRemoteDesktop : IFix
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    public string FindingId => "security.rdp";
    public string Title => "Turn Remote Desktop off";
    public FixRisk Risk => FixRisk.Advanced;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Stops this machine accepting Remote Desktop connections. Only do this if you do not connect to this "
        + "computer remotely — if you do, and you are not sitting in front of it, you will lock yourself out.";

    public string Preview(Finding finding) =>
        $"Set HKLM\\{Key}\\fDenyTSConnections = 1";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would disable Remote Desktop.");

        var previous = Reg.ReadDwordAsString(Registry.LocalMachine, Key, "fDenyTSConnections");
        var undo = new List<UndoStep>
        {
            FixHelpers.Registry("Turn Remote Desktop back on", "HKLM", Key, "fDenyTSConnections", previous, "dword"),
        };

        return Reg.WriteDword(Registry.LocalMachine, Key, "fDenyTSConnections", 1)
            ? FixResult.Ok("Remote Desktop is now off.", undo)
            : new FixResult(false, "Could not write the registry value. Are you running as administrator?", undo);
    }
}

/// <summary>Forces an antivirus definition update.</summary>
public sealed class UpdateDefenderSignatures : IFix
{
    public string FindingId => "security.av-stale";
    public string Title => "Update antivirus definitions now";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => false;
    public bool NothingToUndo => true;

    public string Explain =>
        "Downloads the latest Microsoft Defender definitions immediately rather than waiting for the next "
        + "scheduled check. Takes under a minute on a normal connection.";

    private static string MpCmdRun => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    public string Preview(Finding finding) => $"\"{MpCmdRun}\" -SignatureUpdate";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (!File.Exists(MpCmdRun))
            return FixResult.Failed("Microsoft Defender's command-line tool is not present, so this machine is "
                                    + "probably using a different antivirus. Update it from its own interface.");

        if (ctx.DryRun) return FixResult.Ok("Would update Defender definitions.");

        ctx.Progress("Downloading definitions...");
        var res = FixHelpers.Run(MpCmdRun, "-SignatureUpdate", TimeSpan.FromMinutes(5));

        return res.Ok
            ? FixResult.Ok("Definition update finished.")
            : FixResult.Failed($"The update did not complete: {res.Output}");
    }
}

/// <summary>
/// Removes network shares that expose a drive root or a user profile.
///
/// Deliberately narrow. Removing every share would break the deliberate ones people
/// rely on; these three shapes are the accidental ones.
/// </summary>
public sealed class RemoveRiskyShares : IFix
{
    public string FindingId => "security.shares";
    public string Title => "Remove shares that expose a whole drive or profile";
    public FixRisk Risk => FixRisk.Advanced;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Deletes only the network shares that hand out an entire drive, the Users folder, or the Windows "
        + "folder. Shares of a single specific folder are left alone. Removing a share does not delete any "
        + "files, and this can be undone.";

    internal static List<(string Name, string Path)> Risky(Finding finding)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (finding.Evidence ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Evidence lines are "name<padding>path".
            var split = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length != 2) continue;

            var name = split[0].Trim();
            var path = split[1].Trim();
            if (name.EndsWith("$")) continue;

            var isBroad = path.TrimEnd('\\').Length <= 2
                          || path.Contains(@"\Users", StringComparison.OrdinalIgnoreCase)
                          || path.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase);

            if (isBroad) list.Add((name, path));
        }
        return list;
    }

    public string Preview(Finding finding)
    {
        var shares = Risky(finding);
        if (shares.Count == 0) return "No share exposes a whole drive or profile, so there is nothing to remove.";
        return string.Join("\n", shares.Select(s => $"net share \"{s.Name}\" /delete    :: currently shares {s.Path}"));
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        var shares = Risky(finding);
        if (shares.Count == 0) return FixResult.Failed("No broad shares were found to remove.");
        if (ctx.DryRun) return FixResult.Ok($"Would remove {shares.Count} share(s).");

        var undo = new List<UndoStep>();
        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var (name, path) in shares)
        {
            undo.Add(FixHelpers.Process($"Re-create the share \"{name}\" pointing at {path}",
                "net.exe", $"share \"{name}={path}\""));

            var res = FixHelpers.Run("net.exe", $"share \"{name}\" /delete");
            if (res.Ok) removed.Add(name); else failed.Add($"{name}: {res.Output}");
        }

        if (removed.Count == 0)
            return new FixResult(false, "No share could be removed: " + string.Join("; ", failed), undo);

        var message = $"Removed {removed.Count} share(s): {string.Join(", ", removed)}. No files were deleted.";
        if (failed.Count > 0) message += $" Could not remove: {string.Join("; ", failed)}.";
        return new FixResult(true, message, undo);
    }
}
