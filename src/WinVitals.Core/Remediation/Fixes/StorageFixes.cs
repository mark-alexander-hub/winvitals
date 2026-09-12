using System.Runtime.InteropServices;
using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

/// <summary>
/// Deletes temporary files.
///
/// Irreversible, so it is never bundled into a one-click batch. Only files inside the
/// two temp directories are touched, and only ones older than a day: deleting a temp
/// file an installer is using right now breaks the installer.
/// </summary>
public sealed class CleanTempFiles : IFix
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    public string FindingId => "storage.low.";
    public IReadOnlyList<string> AlsoAppliesTo => new[] { "storage.full." };
    public bool Standalone => true;
    public string Title => "Delete temporary files";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => false;
    public bool NeedsRestart => false;
    public bool Reversible => false;

    public string Explain =>
        "Deletes leftover files from your temp folders that are more than a day old. Programs recreate these "
        + "as needed. This cannot be undone, but nothing here is anything you saved.";

    private static IEnumerable<string> Targets()
    {
        yield return Path.GetTempPath();
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    }

    public string Preview(Finding finding)
    {
        var lines = new List<string>();
        long total = 0;
        foreach (var dir in Targets())
        {
            var size = Measure(dir, out var count);
            total += size;
            lines.Add($"{dir}\n    {count} file(s), {CollectorExtensions.Bytes(size)} older than 24 hours");
        }
        lines.Add($"\nApproximately {CollectorExtensions.Bytes(total)} would be freed.");
        return string.Join("\n", lines);
    }

    private static long Measure(string dir, out int count)
    {
        long size = 0;
        count = 0;
        if (!Directory.Exists(dir)) return 0;

        var cutoff = DateTime.Now - MinimumAge;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                try
                {
                    if (file.LastWriteTime > cutoff) continue;
                    size += file.Length;
                    count++;
                }
                catch { /* vanished mid-walk */ }
            }
        }
        catch { /* whole tree inaccessible */ }

        return size;
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would delete temporary files older than 24 hours.");

        long freed = 0;
        var deleted = 0;
        var locked = 0;
        var cutoff = DateTime.Now - MinimumAge;

        foreach (var dir in Targets())
        {
            if (!Directory.Exists(dir)) continue;
            ctx.Progress($"Cleaning {dir}");

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles("*", options).ToList(); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    if (file.LastWriteTime > cutoff) continue;
                    var size = file.Length;
                    file.Delete();
                    freed += size;
                    deleted++;
                }
                catch
                {
                    // In use by a running program. Expected and harmless.
                    locked++;
                }
            }
        }

        var message = $"Deleted {deleted} file(s) and freed {CollectorExtensions.Bytes(freed)}.";
        if (locked > 0) message += $" {locked} file(s) were in use and left alone.";
        return FixResult.Ok(message);
    }
}

/// <summary>Empties the Recycle Bin on every drive.</summary>
public sealed class EmptyRecycleBin : IFix
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    public string FindingId => "storage.low.";
    public IReadOnlyList<string> AlsoAppliesTo => new[] { "storage.full." };
    public bool Standalone => true;
    public string Title => "Empty the Recycle Bin";
    public FixRisk Risk => FixRisk.Advanced;
    public bool NeedsElevation => false;
    public bool NeedsRestart => false;
    public bool Reversible => false;

    public string Explain =>
        "Permanently deletes everything in the Recycle Bin on every drive. Check the bin first if you are not "
        + "certain what is in it — after this, those files are gone.";

    public string Preview(Finding finding) =>
        "Empties the Recycle Bin on all drives. This cannot be undone.";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would empty the Recycle Bin.");

        var code = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);

        // 0 is success; -2147418113 (E_UNEXPECTED) is what an already-empty bin returns.
        return code is 0 or unchecked((int)0x8000FFFF)
            ? FixResult.Ok("The Recycle Bin is now empty.")
            : FixResult.Failed($"Windows returned error 0x{code:X8} while emptying the Recycle Bin.");
    }
}

/// <summary>Reclaims the hibernation file.</summary>
public sealed class DisableHibernateForSpace : IFix
{
    public string FindingId => "storage.hiberfil";
    public string Title => "Turn off hibernate and reclaim the space";
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Deletes the hibernation file and frees the space it was holding. Two consequences: the machine can no "
        + "longer hibernate, and Fast Startup stops working, so shutting down becomes a real shutdown and takes "
        + "a few seconds longer to boot. Fully reversible.";

    public string Preview(Finding finding) => "powercfg /hibernate off";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would turn hibernate off and delete the hibernation file.");

        var undo = new List<UndoStep>
        {
            FixHelpers.Process("Turn hibernate back on", "powercfg.exe", "/hibernate on"),
        };

        var res = FixHelpers.Run("powercfg.exe", "/hibernate off");
        return res.Ok
            ? FixResult.Ok("Hibernate is off and the hibernation file has been removed.", undo)
            : new FixResult(false, $"powercfg refused: {res.Output}", undo);
    }
}

/// <summary>Re-enables TRIM notifications for SSDs.</summary>
public sealed class EnableTrim : IFix
{
    public string FindingId => "storage.trim";
    public string Title => "Turn TRIM back on";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Lets Windows tell the SSD which blocks are free again. Without this an SSD slowly gets slower as it "
        + "loses track of what it can reuse.";

    public string Preview(Finding finding) => "fsutil behavior set DisableDeleteNotify 0";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would enable TRIM.");

        var undo = new List<UndoStep>
        {
            FixHelpers.Process("Disable TRIM again", "fsutil.exe", "behavior set DisableDeleteNotify 1"),
        };

        var res = FixHelpers.Run("fsutil.exe", "behavior set DisableDeleteNotify 0");
        return res.Ok
            ? FixResult.Ok("TRIM is enabled.", undo)
            : new FixResult(false, $"fsutil refused: {res.Output}", undo);
    }
}
