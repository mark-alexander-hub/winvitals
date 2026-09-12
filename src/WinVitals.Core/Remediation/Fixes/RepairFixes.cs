using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

/// <summary>
/// Repairs corrupted Windows system files with DISM, then SFC.
///
/// The order matters and is the reason people run sfc /scannow twice and give up: SFC
/// repairs from the local component store, so if that store is itself damaged SFC
/// reports "could not fix" forever. DISM repairs the store first.
/// </summary>
public sealed class RepairSystemFiles : IFix
{
    public string FindingId => "reliability.bsod";
    public IReadOnlyList<string> AlsoAppliesTo => new[] { "reliability.disk-errors", "updates.stale" };
    public bool Standalone => true;

    public string Title => "Repair damaged Windows system files";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Checks Windows' own files for damage and repairs anything broken from Microsoft's copies. This is the "
        + "standard first step for crashes, failed updates and features that stop working. It is safe and "
        + "touches nothing of yours, but it is slow: usually 10 to 20 minutes, occasionally longer. Leave the "
        + "machine plugged in and do not close the window.";

    public string Preview(Finding finding) =>
        "DISM /Online /Cleanup-Image /RestoreHealth\n"
        + "sfc /scannow\n\n"
        + "DISM runs first on purpose: SFC repairs from the local component store, so if that store is itself\n"
        + "damaged SFC will keep reporting failures until DISM has fixed it.";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would run DISM RestoreHealth followed by SFC.");

        var timeout = TimeSpan.FromMinutes(40);

        ctx.Progress("Repairing the component store with DISM (this takes a while)...");
        var dism = FixHelpers.Run("dism.exe", "/Online /Cleanup-Image /RestoreHealth", timeout);

        ctx.Progress("Checking system files with SFC...");
        var sfc = FixHelpers.Run("sfc.exe", "/scannow", timeout);

        var repaired = sfc.Output.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase);
        var clean = sfc.Output.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase);
        var unfixable = sfc.Output.Contains("unable to fix", StringComparison.OrdinalIgnoreCase);

        var message =
            clean ? "No damaged system files were found. Windows' own files are intact."
            : repaired ? "Damaged system files were found and repaired. Restart to be sure everything reloads."
            : unfixable ? "Some damaged files could not be repaired automatically. The detail is in "
                          + @"C:\Windows\Logs\CBS\CBS.log."
            : "The repair finished. See the output below for what it reported.";

        if (!dism.Ok)
            message += " DISM reported a problem, which usually means no internet connection or a "
                       + "Windows Update service that is stopped.";

        return new FixResult(true, message, Array.Empty<UndoStep>(), RestartNeeded: repaired);
    }
}

/// <summary>Rebuilds the TCP/IP and Winsock configuration.</summary>
public sealed class ResetNetworkStack : IFix
{
    public string FindingId => "network.offline";
    public bool Standalone => true;

    public string Title => "Reset the network settings";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => true;
    public bool Reversible => false;

    public string Explain =>
        "Rebuilds Windows' networking configuration from scratch. This fixes most \"connected but no internet\" "
        + "problems that survive a reboot. It needs a restart to finish, and it clears manually configured IP "
        + "addresses, static DNS servers and VPN client settings, so you may need to set those up again.";

    public string Preview(Finding finding) =>
        "netsh winsock reset\n"
        + "netsh int ip reset\n"
        + "ipconfig /flushdns\n"
        + "ipconfig /registerdns\n\n"
        + "A restart is required afterwards.";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would reset Winsock and the TCP/IP stack.");

        ctx.Progress("Resetting Winsock...");
        var winsock = FixHelpers.Run("netsh.exe", "winsock reset");

        ctx.Progress("Resetting TCP/IP...");
        var ip = FixHelpers.Run("netsh.exe", "int ip reset");

        ctx.Progress("Flushing DNS...");
        FixHelpers.Run("ipconfig.exe", "/flushdns");
        FixHelpers.Run("ipconfig.exe", "/registerdns");

        if (!winsock.Ok && !ip.Ok)
            return FixResult.Failed($"Neither reset succeeded: {winsock.Output} {ip.Output}");

        return FixResult.Ok(
            "Network settings have been reset. You must restart the machine for this to take effect.",
            Array.Empty<UndoStep>(), restart: true);
    }
}

/// <summary>Clears the DNS resolver cache.</summary>
public sealed class FlushDnsCache : IFix
{
    public string FindingId => "network.dns";
    public bool Standalone => true;

    public string Title => "Clear the DNS cache";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => false;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Forgets remembered website addresses so Windows looks them up fresh. Fixes the case where one site "
        + "will not load while everything else works, usually after a site has moved server. Instant and harmless.";

    public string Preview(Finding finding) => "ipconfig /flushdns";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would flush the DNS cache.");

        var res = FixHelpers.Run("ipconfig.exe", "/flushdns");
        return res.Ok
            ? FixResult.Ok("DNS cache cleared.")
            : FixResult.Failed($"Could not flush the cache: {res.Output}");
    }
}

/// <summary>
/// Clears the Windows Update download cache.
///
/// Only the Download folder is removed, not the whole SoftwareDistribution directory:
/// wiping all of it also throws away update history and the machine's servicing state,
/// which is a far bigger change than the "reset Windows Update" advice usually admits.
/// </summary>
public sealed class ResetWindowsUpdateCache : IFix
{
    public string FindingId => "updates.stale";
    public bool Standalone => true;

    public string Title => "Clear the Windows Update download cache";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => false;

    public string Explain =>
        "Stops Windows Update, deletes its partly-downloaded files, and starts it again. Fixes updates that "
        + "fail repeatedly with the same error. Windows re-downloads whatever it needs, so the next update "
        + "check will use more data than usual. Your update history is kept.";

    private static string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");

    public string Preview(Finding finding) =>
        "net stop wuauserv\n"
        + "net stop bits\n"
        + $"delete the contents of {DownloadFolder}\n"
        + "net start bits\n"
        + "net start wuauserv";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would clear the Windows Update download cache.");

        ctx.Progress("Stopping Windows Update...");
        FixHelpers.Run("net.exe", "stop wuauserv", TimeSpan.FromMinutes(2));
        FixHelpers.Run("net.exe", "stop bits", TimeSpan.FromMinutes(2));

        long freed = 0;
        var failures = 0;

        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                ctx.Progress("Deleting cached update files...");
                foreach (var entry in new DirectoryInfo(DownloadFolder).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry is FileInfo file)
                        {
                            freed += file.Length;
                            file.Delete();
                        }
                        else if (entry is DirectoryInfo dir)
                        {
                            dir.Delete(recursive: true);
                        }
                    }
                    catch { failures++; }
                }
            }
        }
        finally
        {
            // Always bring the services back, even if deletion went badly. Leaving
            // Windows Update stopped would be a worse state than the one we started in.
            ctx.Progress("Restarting Windows Update...");
            FixHelpers.Run("net.exe", "start bits", TimeSpan.FromMinutes(2));
            FixHelpers.Run("net.exe", "start wuauserv", TimeSpan.FromMinutes(2));
        }

        var message = $"Cleared the update cache and freed about {CollectorExtensions.Bytes(freed)}.";
        if (failures > 0) message += $" {failures} item(s) were locked and left in place.";
        return FixResult.Ok(message);
    }
}
