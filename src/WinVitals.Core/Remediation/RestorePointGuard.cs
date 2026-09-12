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
/// The throttle lives in SystemRestorePointCreationFrequency, in minutes. This class
/// sets it to zero for the duration of the call and puts the original value back.
/// </summary>
public static class RestorePointGuard
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ThrottleValue = "SystemRestorePointCreationFrequency";

    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    public static RestorePointOutcome Ensure(string description, bool elevated)
    {
        if (!elevated)
        {
            return RestorePointOutcome.No(
                "No restore point was created",
                "Creating a restore point needs administrator rights and WinVitals is running as a "
                + "standard user.");
        }

        var before = LatestSequence();
        var throttle = ReadThrottle();
        var throttleChanged = false;

        try
        {
            if (throttle != 0)
            {
                throttleChanged = WriteThrottle(0);
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

            var after = LatestSequence();
            var created = after.HasValue && (!before.HasValue || after.Value > before.Value);

            if (created)
            {
                return new RestorePointOutcome(
                    true,
                    "Restore point created and verified",
                    $"A new restore point exists (sequence {after}). If a repair causes trouble you can "
                    + "roll the system back to this point.");
            }

            // The call reported success and nothing appeared. Report the truth.
            return RestorePointOutcome.No(
                "Windows reported success but no restore point appeared",
                $"The restore point count did not change (still sequence {before?.ToString() ?? "none"}). "
                + "This usually means System Protection has no disk space allocated, or a policy is "
                + "blocking it. Treat this as having no safety net: WinVitals will not pretend otherwise.");
        }
        catch (Exception ex)
        {
            return RestorePointOutcome.No("Could not create a restore point", ex.Message);
        }
        finally
        {
            if (throttleChanged) WriteThrottle(throttle);
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

    private static int ReadThrottle()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SrKey);
            var value = key?.GetValue(ThrottleValue);
            // Absent means the Windows default of 1440 minutes, i.e. one per day.
            return value is null ? 1440 : Convert.ToInt32(value);
        }
        catch
        {
            return 1440;
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
}
