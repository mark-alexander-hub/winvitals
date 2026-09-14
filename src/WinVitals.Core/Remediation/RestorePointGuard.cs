using System.Management;
using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Remediation;

public sealed record RestorePointOutcome(
    bool Created,
    string Summary,
    string Detail,
    bool ProtectionDisabled = false)
{
    public static RestorePointOutcome No(string summary, string detail, bool disabled = false) =>
        new(false, summary, detail, disabled);
}

/// <summary>
/// Creates a system restore point before any repair runs, and then checks that one
/// actually appeared.
///
/// The checking is the point. Windows silently refuses to create a restore point if
/// another was made within the last 24 hours: the API returns success, nothing is
/// created, and the caller believes it has a safety net it does not have. Every
/// "create a restore point first" script that does not verify afterwards is lying to
/// its user some of the time.
///
/// Two things this class learned the hard way on real hardware:
///
///   * CreateRestorePoint returns before the new point is visible to a WMI query.
///     Counting immediately reports "nothing was created" for a point that exists —
///     a false negative, which is every bit as bad as the false reassurance this
///     class exists to prevent. So the count is polled, not sampled once.
///
///   * Suspending the 24-hour throttle means writing a machine-wide registry value.
///     A finally block does not run when a process is killed, so the original value
///     is written to disk before it is touched and recovered on the next attempt.
/// </summary>
public static class RestorePointGuard
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ThrottleValue = "SystemRestorePointCreationFrequency";

    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>How long to keep looking before accepting that nothing was created.</summary>
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Holds the throttle value we are about to overwrite, in case we are killed.</summary>
    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinVitals", "throttle-restore.txt");

    public static RestorePointOutcome Ensure(string description, bool elevated)
    {
        if (!elevated)
        {
            return RestorePointOutcome.No(
                "No restore point was created",
                "Creating a restore point needs administrator rights and WinVitals is running as a "
                + "standard user.");
        }

        // If a previous attempt was killed between changing the throttle and putting it
        // back, undo that first — otherwise this run reads the leftover value as though
        // it were the machine's own setting and never restores anything.
        RecoverInterruptedThrottle();

        var before = LatestSequence();
        var original = ReadThrottle();
        var throttleChanged = false;

        try
        {
            if (original != 0)
            {
                SaveState(original);
                throttleChanged = WriteThrottle(0);
                if (!throttleChanged) ClearState();
            }

            var invokeError = Create(description);

            if (invokeError is not null)
            {
                var disabled = invokeError.Contains("0x80070422")
                               || invokeError.Contains("disabled", StringComparison.OrdinalIgnoreCase);

                return RestorePointOutcome.No(
                    disabled ? "System Protection is switched off" : "Could not create a restore point",
                    disabled
                        ? "System Protection is disabled for this drive, so Windows cannot take restore "
                          + "points at all. You can turn it on in System Properties > System Protection. "
                          + "Note that it protects system files and settings, not your documents."
                        : $"Windows refused the request: {invokeError}",
                    disabled);
            }

            var after = WaitForNewPoint(before);

            if (after.HasValue)
            {
                return new RestorePointOutcome(
                    true,
                    "Restore point created and verified",
                    $"A new restore point exists (sequence {after}). If a repair causes trouble you can "
                    + "roll the system back to this point.");
            }

            // The call reported success and, after waiting, still nothing is listed.
            // Report exactly that, without asserting a cause: the common ones are the
            // 24-hour throttle, no space allocated to System Protection, and policy —
            // but guessing between them in the message has been wrong before.
            return RestorePointOutcome.No(
                "Windows reported success but no restore point appeared",
                $"The restore point list did not change in the {VisibilityTimeout.TotalSeconds:0} seconds "
                + $"after the request (still at sequence {before?.ToString() ?? "none"}). Treat this as "
                + "having no safety net: WinVitals will not pretend otherwise. Checking System Properties "
                + "> System Protection will show whether it is switched on for this drive and how much "
                + "space it is allowed.");
        }
        catch (Exception ex)
        {
            return RestorePointOutcome.No("Could not create a restore point", ex.Message);
        }
        finally
        {
            if (throttleChanged)
            {
                RestoreThrottle(original);
                ClearState();
            }
        }
    }

    /// <summary>
    /// Re-reads the restore point list until a newer one appears or time runs out.
    /// Returns the new highest sequence, or null if none arrived.
    /// </summary>
    private static long? WaitForNewPoint(long? before)
    {
        var deadline = DateTime.UtcNow + VisibilityTimeout;

        while (true)
        {
            var after = LatestSequence();
            if (after.HasValue && (!before.HasValue || after.Value > before.Value)) return after;

            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>Returns null on success, or the error text.</summary>
    private static string? Create(string description)
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var result = cls.InvokeMethod("CreateRestorePoint",
                new object[] { description, ModifySettings, BeginSystemChange });

            var code = Convert.ToUInt32(result ?? 0u);
            return code == 0 ? null : $"error code {code}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static long? LatestSequence()
    {
        var points = Wmi.Query("SELECT SequenceNumber FROM SystemRestore", @"root\default");
        if (points.Count == 0) return null;
        return points.Max(p => p.Num("SequenceNumber"));
    }

    // ------------------------------------------------------------- throttle

    /// <summary>The throttle in minutes, or null when the value is absent (the default).</summary>
    private static int? ReadThrottle()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SrKey);
            var value = key?.GetValue(ThrottleValue);
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteThrottle(int minutes)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SrKey, writable: true);
            if (key is null) return false;
            key.SetValue(ThrottleValue, minutes, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the throttle back. A value that did not exist before is deleted rather
    /// than set to the documented default: writing 1440 where Windows had nothing
    /// leaves the machine in a state it was never in.
    /// </summary>
    private static void RestoreThrottle(int? original)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SrKey, writable: true);
            if (key is null) return;

            if (original.HasValue) key.SetValue(ThrottleValue, original.Value, RegistryValueKind.DWord);
            else key.DeleteValue(ThrottleValue, throwOnMissingValue: false);
        }
        catch
        {
            // Nothing useful to do; the state file is left in place so the next run retries.
        }
    }

    // ------------------------------------------------------- crash recovery

    internal const string AbsentMarker = "absent";

    private static void SaveState(int? original)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, original?.ToString() ?? AbsentMarker);
        }
        catch
        {
            // Best effort. Losing the note only costs us crash recovery, not correctness
            // of this run, whose finally block will still restore the value.
        }
    }

    private static void ClearState()
    {
        try { File.Delete(StatePath); } catch { /* nothing to clear */ }
    }

    /// <summary>
    /// Restores a throttle value left behind by a run that was killed before its
    /// finally block could execute. Safe to call at any time; does nothing when there
    /// is no note on disk.
    /// </summary>
    public static void RecoverInterruptedThrottle()
    {
        string note;
        try
        {
            if (!File.Exists(StatePath)) return;
            note = File.ReadAllText(StatePath).Trim();
        }
        catch
        {
            return;
        }

        RestoreThrottle(ParseState(note));
        ClearState();
    }

    /// <summary>Parses a saved throttle note. Anything unrecognised means "was absent".</summary>
    internal static int? ParseState(string note) =>
        int.TryParse(note, out var minutes) ? minutes : null;
}
