using System.Diagnostics;
using WinVitals.Core;

namespace WinVitals.Collectors;

/// <summary>
/// Disk health, free space, and what is eating the space.
///
/// The "what is eating it" half matters more than it sounds. Windows has several
/// features that quietly consume tens of gigabytes on a drive the user never looks
/// at — File History and the old Windows 7 backup engine are the worst offenders,
/// and neither announces itself when the drive fills up.
/// </summary>
public sealed class StorageCollector : ICollector
{
    public string Id => "storage";
    public string Name => "Storage";
    public string Blurb => "Disk health, free space, and which Windows features are consuming it.";

    public void Collect(ScanContext ctx, ModuleResult m)
    {
        Drives(m);
        Volumes(m);
        SmartHealth(ctx, m);
        SpaceEaters(m);
        Trim(m);
    }

    private static void Drives(ModuleResult m)
    {
        // MSFT_PhysicalDisk knows SSD vs HDD and carries a health status; Win32_DiskDrive
        // does not, but is present even when the Storage provider is unavailable.
        var physical = Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");

        if (physical.Count > 0)
        {
            foreach (var d in physical)
            {
                var model = d.Str("FriendlyName");
                var media = d.Num("MediaType") switch
                {
                    3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "unspecified"
                };
                var health = d.Num("HealthStatus") switch
                {
                    0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "unknown"
                };
                var size = CollectorExtensions.Bytes(d.Num("Size"));

                m.Fact($"Disk: {model}", $"{size}, {media}, health {health}");

                if (d.Num("HealthStatus") is 1 or 2)
                {
                    m.Add(Severity.Critical, $"storage.health.{Slug(model)}", $"Disk \"{model}\" reports itself as {health.ToLowerInvariant()}",
                        what: $"Windows Storage reports HealthStatus = {health} for this {media}.",
                        why: "The drive itself is saying it is degrading. Drives that report this often fail "
                             + "within weeks, and the failure usually takes everything not backed up with it.",
                        action: "Back up anything you cannot lose today, before doing anything else. Then plan a "
                                + "replacement. Do not run disk benchmarks or defragmentation on a drive in this state.",
                        evidence: $"FriendlyName : {model}\nMediaType    : {media}\nHealthStatus : {health}\nSize         : {size}");
                }

                if (media == "HDD" && IsSystemDisk(d))
                {
                    m.Add(Severity.Advisory, "storage.hdd-system", "Windows is installed on a spinning hard disk",
                        what: $"The system drive is a mechanical HDD ({model}).",
                        why: "On a modern Windows build this is the single largest cause of a slow machine. "
                             + "No amount of software tuning closes the gap: a mechanical disk serves roughly "
                             + "one hundredth of the random reads an SSD does, and Windows does a great many "
                             + "small random reads.",
                        action: "If the machine feels slow and you can change one thing, replace this disk with "
                                + "an SSD. It will outperform any other upgrade, including more memory.",
                        evidence: $"{model} — {size}, mechanical");
                }
            }
        }
        else
        {
            foreach (var d in Wmi.Query("SELECT * FROM Win32_DiskDrive"))
                m.Fact($"Disk: {d.Str("Model")}", CollectorExtensions.Bytes(d.Num("Size")));
        }
    }

    private static bool IsSystemDisk(System.Management.ManagementBaseObject disk)
    {
        // DeviceId 0 is not a guarantee, but on a single-disk laptop it is right, and
        // this only decides whether to show advice, never a destructive action.
        return disk.Str("DeviceId") == "0";
    }

    private static void Volumes(ModuleResult m)
    {
        foreach (var v in Wmi.Query("SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            var letter = v.Str("DeviceID");
            var size = v.Num("Size");
            var free = v.Num("FreeSpace");
            if (size <= 0) continue;

            var pct = free * 100.0 / size;
            var label = v.Str("VolumeName");
            var name = string.IsNullOrWhiteSpace(label) ? letter : $"{letter} ({label})";

            m.Fact($"Volume {name}",
                $"{CollectorExtensions.Bytes(free)} free of {CollectorExtensions.Bytes(size)}",
                $"{pct:0.#}% free");

            var evidence = $"Volume    : {name}\nCapacity  : {CollectorExtensions.Bytes(size)}\n"
                         + $"Free      : {CollectorExtensions.Bytes(free)} ({pct:0.#}%)";

            var isSystem = letter.Equals(
                Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (pct < 5 || free < 3L * 1024 * 1024 * 1024)
            {
                m.Add(Severity.Critical, $"storage.full.{letter.TrimEnd(':')}", $"Drive {name} is nearly full",
                    what: $"Only {CollectorExtensions.Bytes(free)} free ({pct:0.#}%).",
                    why: isSystem
                        ? "Below roughly 10% free, Windows cannot reliably install updates, grow the page file, "
                          + "or create restore points. Below 5% it starts failing at ordinary work and the whole "
                          + "machine slows down."
                        : "A drive this full will fail writes without much warning.",
                    action: "Free space now. Storage Sense and Disk Cleanup are the safe first steps; the "
                            + "space-consumer check below shows the usual large culprits.",
                    command: "cleanmgr /d " + letter,
                    evidence: evidence);
            }
            else if (pct < 12)
            {
                m.Add(Severity.Warning, $"storage.low.{letter.TrimEnd(':')}", $"Drive {name} is running low on space",
                    what: $"{CollectorExtensions.Bytes(free)} free ({pct:0.#}%).",
                    why: "Windows wants roughly 10-15% headroom on the system drive for updates, the page file "
                         + "and restore points.",
                    action: "Reclaim some space before it becomes urgent.",
                    command: "cleanmgr /d " + letter,
                    evidence: evidence);
            }
            else
            {
                m.Ok($"storage.space.{letter.TrimEnd(':')}", $"Drive {name} has enough free space",
                    $"{CollectorExtensions.Bytes(free)} free ({pct:0.#}%).", evidence);
            }
        }
    }

    private static void SmartHealth(ScanContext ctx, ModuleResult m)
    {
        if (!ctx.Elevated)
        {
            m.NeedsAdmin("storage.smart", "Cannot read the drive's own failure prediction",
                "SMART failure prediction needs administrator rights. This is the check that warns you before "
                + "a disk dies.");
            return;
        }

        var status = Wmi.Query("SELECT * FROM MSStorageDriver_FailurePredictStatus", @"root\wmi");
        if (status.Count == 0)
        {
            m.Add(Severity.Unknown, "storage.smart", "SMART data is not exposed by this machine",
                "The storage driver does not publish failure prediction to Windows.",
                why: "Common on NVMe drives and on laptops using certain RAID or Intel RST drivers. It is not "
                     + "itself a fault, but it does mean Windows cannot warn you before this disk fails.",
                action: "Use the drive vendor's own tool for health, or check the NVMe wear indicators with a "
                        + "third-party SMART reader.",
                evidence: "MSStorageDriver_FailurePredictStatus returned no instances.");
            return;
        }

        var failing = status.Where(s => s.Flag("PredictFailure")).ToList();
        if (failing.Count > 0)
        {
            m.Add(Severity.Critical, "storage.smart", "A drive is predicting its own failure",
                what: $"{failing.Count} drive(s) report PredictFailure = true.",
                why: "SMART has crossed a vendor threshold that means imminent failure. This is the most "
                     + "serious warning a disk can give.",
                action: "Copy your data off today. Replace the drive. Do not wait for it to get worse.",
                evidence: string.Join("\n", failing.Select(f => f.Str("InstanceName"))));
        }
        else
        {
            m.Ok("storage.smart", "No drive is predicting failure",
                $"SMART failure prediction is clear on {status.Count} drive(s).",
                string.Join("\n", status.Select(s => $"{s.Str("InstanceName")}: PredictFailure=False")));
        }
    }

    /// <summary>
    /// Looks for the Windows features that silently consume large amounts of space.
    /// These are reported as facts and one advisory, never as a fault: a full backup
    /// drive is doing its job, it just should not be a surprise.
    /// </summary>
    private static void SpaceEaters(ModuleResult m)
    {
        var found = new List<(string What, string Path, long Size)>();

        var hiberfil = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys");
        var hiberSize = FileSize(hiberfil);
        if (hiberSize > 0) found.Add(("Hibernation file", hiberfil, hiberSize));

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            // File History and the legacy Windows Backup engine both write to a drive
            // the user chose once and then forgot about. This is the check that would
            // have explained a backup drive filling to capacity on its own.
            TryAdd(found, "File History backups", Path.Combine(drive.Name, "FileHistory"));
            TryAdd(found, "Windows image backups", Path.Combine(drive.Name, "WindowsImageBackup"));
            TryAdd(found, "Recycle Bin", Path.Combine(drive.Name, "$Recycle.Bin"));
        }

        foreach (var (what, path, size) in found.OrderByDescending(f => f.Size))
            m.Fact(what, CollectorExtensions.Bytes(size), path);

        var backups = found.Where(f => f.What.Contains("backup", StringComparison.OrdinalIgnoreCase)).ToList();
        var backupTotal = backups.Sum(b => b.Size);

        if (backupTotal > 20L * 1024 * 1024 * 1024)
        {
            m.Add(Severity.Advisory, "storage.backup-growth", "Windows backup features are holding a lot of space",
                what: string.Join("; ", backups.Select(b => $"{b.What} at {b.Path} is using {CollectorExtensions.Bytes(b.Size)}"))
                      + $" — {CollectorExtensions.Bytes(backupTotal)} in total.",
                why: "File History and Windows Backup keep every version they have ever taken until the drive "
                     + "is full. They do not warn you first, and because they target a drive you rarely open, "
                     + "the usual symptom is a second disk that mysteriously has no space left.",
                action: "If you use these, this is normal and you can cap the retention. If you did not know "
                        + "they were running, turn off the one you do not want and delete its folder. Make sure "
                        + "you have a backup you actually rely on before deleting anything.",
                command: "control /name Microsoft.FileHistory",
                evidence: string.Join("\n", found.Select(f => $"{f.Path,-48} {CollectorExtensions.Bytes(f.Size)}")));
        }

        if (hiberSize > 8L * 1024 * 1024 * 1024)
        {
            m.Add(Severity.Advisory, "storage.hiberfil", $"The hibernation file is using {CollectorExtensions.Bytes(hiberSize)}",
                what: $"hiberfil.sys is {CollectorExtensions.Bytes(hiberSize)}.",
                why: "Windows reserves this to hold the contents of memory when hibernating. It scales with "
                     + "installed RAM, so on a large-memory machine it is substantial.",
                action: "Keep it if you use hibernate or Fast Startup. If you use neither, turning hibernate "
                        + "off reclaims the whole file immediately.",
                command: "powercfg /hibernate off",
                undo: "powercfg /hibernate on",
                evidence: $"{hiberfil} = {CollectorExtensions.Bytes(hiberSize)}");
        }
    }

    private static void TryAdd(List<(string, string, long)> found, string label, string path)
    {
        if (!Directory.Exists(path)) return;
        var size = DirectorySize(path);
        if (size > 512L * 1024 * 1024) found.Add((label, path, size));
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Sums a directory tree, giving up after a few seconds. A precise number is not
    /// worth making the user wait: "at least 40 GB" answers the question just as well.
    /// </summary>
    private static long DirectorySize(string path)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try { total += file.Length; } catch { /* vanished mid-walk */ }
                if (sw.Elapsed > TimeSpan.FromSeconds(4)) break;
            }
        }
        catch
        {
            // Access denied on the whole tree; report what we managed to add.
        }
        return total;
    }

    private static void Trim(ModuleResult m)
    {
        var res = Shell.Run("fsutil.exe", "behavior query DisableDeleteNotify");
        if (!res.Ok) return;

        // 0 means TRIM notifications are enabled. Modern Windows reports one line per
        // filesystem (NTFS and ReFS); any enabled line is good enough to call it on.
        var disabled = res.Lines
            .Where(l => l.Contains("DisableDeleteNotify", StringComparison.OrdinalIgnoreCase))
            .All(l => l.TrimEnd().EndsWith("1"));

        if (disabled && res.Lines.Any())
        {
            m.Add(Severity.Advisory, "storage.trim", "TRIM is switched off",
                what: "DisableDeleteNotify is set, so Windows is not telling the SSD which blocks are free.",
                why: "Without TRIM an SSD gradually slows down as it loses track of which blocks it can reuse.",
                action: "Turn TRIM back on unless you had a specific reason to disable it.",
                command: "fsutil behavior set DisableDeleteNotify 0",
                undo: "fsutil behavior set DisableDeleteNotify 1",
                evidence: res.Text);
        }
        else
        {
            m.Ok("storage.trim", "TRIM is enabled", "Windows is notifying SSDs of freed blocks.", res.Text);
        }
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
