using System.Diagnostics;
using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

/// <summary>
/// Deletes the previous Windows installation kept for rolling back an update.
///
/// The folder is owned by TrustedInstaller, so a plain delete fails. Ownership is
/// taken first, the way Disk Cleanup does it under the hood.
/// </summary>
public sealed class RemoveWindowsOld : IFix
{
    public string FindingId => "storage.windows-old";
    public bool Standalone => true;
    public string Title => "Remove the previous Windows installation";
    public FixRisk Risk => FixRisk.Advanced;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => false;

    public string Explain =>
        "Deletes C:\\Windows.old, the copy of Windows kept so the last big update can be rolled back. "
        + "Usually 15-30 GB. After this, going back to the previous version of Windows is impossible. "
        + "Your own files are not in this folder.";

    private static string Folder => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Windows.old");

    public string Preview(Finding finding)
    {
        if (!Directory.Exists(Folder)) return $"{Folder} does not exist. Nothing to remove.";
        return $"takeown /F \"{Folder}\" /R /A /D Y\n"
             + $"icacls \"{Folder}\" /grant Administrators:F /T /C\n"
             + $"rd /s /q \"{Folder}\"";
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (!Directory.Exists(Folder)) return FixResult.Failed($"{Folder} does not exist, so there is nothing to remove.");
        if (ctx.DryRun) return FixResult.Ok("Would remove Windows.old.");

        long before = 0;
        try { before = new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace; } catch { }

        ctx.Progress("Taking ownership (this can take a few minutes)...");
        FixHelpers.Run("takeown.exe", $"/F \"{Folder}\" /R /A /D Y", TimeSpan.FromMinutes(15));
        FixHelpers.Run("icacls.exe", $"\"{Folder}\" /grant Administrators:F /T /C /Q", TimeSpan.FromMinutes(15));

        ctx.Progress("Deleting...");
        var rd = FixHelpers.Run("cmd.exe", $"/c rd /s /q \"{Folder}\"", TimeSpan.FromMinutes(20));

        if (Directory.Exists(Folder))
            return FixResult.Failed("Some files could not be removed. Windows may still be using them; "
                                    + "try again after a restart. " + rd.Output);

        long after = 0;
        try { after = new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace; } catch { }
        var freed = after > before ? CollectorExtensions.Bytes(after - before) : "space";
        return FixResult.Ok($"Windows.old is gone and {freed} was reclaimed.");
    }
}

/// <summary>Clears the Delivery Optimization download cache.</summary>
public sealed class ClearDeliveryOptimizationCache : IFix
{
    public string FindingId => "storage.do-cache";
    public bool Standalone => true;
    public string Title => "Clear the update sharing cache";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => false;
    public bool NothingToUndo => true;

    public string Explain =>
        "Delivery Optimization keeps copies of updates it has downloaded so it can share them with other "
        + "PCs on your network. The cache can quietly grow to several gigabytes. Clearing it costs nothing: "
        + "Windows downloads again if it ever needs to.";

    public string Preview(Finding finding) =>
        "powershell -NoProfile -Command \"Delete-DeliveryOptimizationCache -Force\"";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would clear the Delivery Optimization cache.");

        var res = FixHelpers.Run("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Delete-DeliveryOptimizationCache -Force\"",
            TimeSpan.FromMinutes(3));

        return res.Ok
            ? FixResult.Ok("The update sharing cache is cleared.")
            : FixResult.Failed($"Windows refused: {res.Output}");
    }
}

/// <summary>
/// Removes superseded versions of Windows components from the WinSxS store.
///
/// StartComponentCleanup without /ResetBase is the safe form: it drops components no
/// installed update still refers to and keeps the ability to uninstall recent
/// updates. /ResetBase reclaims more and is deliberately not used.
/// </summary>
public sealed class ComponentStoreCleanup : IFix
{
    public string FindingId => "storage.component-store";
    public bool Standalone => true;
    public string Title => "Clean up old Windows update components";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => false;
    public bool NothingToUndo => true;

    public string Explain =>
        "Every Windows update leaves the version it replaced in the component store, in case it is "
        + "uninstalled. This removes the ones nothing refers to any more. Typically reclaims 1-5 GB. "
        + "Slow — five to fifteen minutes — and it keeps your ability to uninstall recent updates.";

    public string Preview(Finding finding) => "DISM /Online /Cleanup-Image /StartComponentCleanup";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would clean up the component store.");

        long before = 0;
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        try { before = new DriveInfo(root).AvailableFreeSpace; } catch { }

        ctx.Progress("Cleaning up the component store (this takes a while)...");
        var res = FixHelpers.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromMinutes(40));

        long after = 0;
        try { after = new DriveInfo(root).AvailableFreeSpace; } catch { }
        var freed = after > before ? $" About {CollectorExtensions.Bytes(after - before)} was reclaimed." : "";

        return res.Ok
            ? FixResult.Ok("Component store cleanup finished." + freed)
            : FixResult.Failed($"DISM reported a problem: {res.Output}");
    }
}
