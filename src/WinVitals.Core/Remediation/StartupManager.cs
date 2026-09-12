using Microsoft.Win32;

namespace WinVitals.Remediation;

public sealed record StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }

    /// <summary>Where Windows launches it from, in words: "All users", "You", "Startup folder".</summary>
    public required string Origin { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>"HKCU" or "HKLM" — which hive holds the approval flag.</summary>
    public required string Hive { get; init; }

    /// <summary>Registry path of the StartupApproved key that records enabled state.</summary>
    public required string ApprovalKey { get; init; }
}

/// <summary>
/// Lists and toggles the programs that start at sign-in.
///
/// Enabling and disabling is done exactly the way Task Manager does it: by writing the
/// StartupApproved flag rather than deleting the Run entry. That matters because it is
/// reversible and because the program's own installer will not silently put back an
/// entry the user disabled — whereas a deleted Run value usually reappears.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static readonly byte[] EnabledBlob = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DisabledBlob = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        Collect(Registry.CurrentUser, "HKCU", "You", items);
        Collect(Registry.LocalMachine, "HKLM", "All users", items);
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Collect(RegistryKey hive, string hiveName, string origin, List<StartupItem> into)
    {
        try
        {
            using var run = hive.OpenSubKey(RunKey);
            if (run is null) return;

            using var approved = hive.OpenSubKey(ApprovedRun);

            foreach (var name in run.GetValueNames())
            {
                var command = run.GetValue(name)?.ToString() ?? "";

                // Absent from StartupApproved means it has never been toggled, which
                // Windows treats as enabled.
                var flag = approved?.GetValue(name) as byte[];
                var enabled = flag is null || flag.Length == 0 || (flag[0] & 1) == 0;

                into.Add(new StartupItem
                {
                    Name = name,
                    Command = command,
                    Origin = origin,
                    Enabled = enabled,
                    Hive = hiveName,
                    ApprovalKey = ApprovedRun,
                });
            }
        }
        catch
        {
            // A hive we cannot read simply contributes nothing.
        }
    }

    /// <summary>
    /// Turns one startup entry on or off. Returns the undo step that reverses it, so
    /// the caller can record it before the change is visible to the user.
    /// </summary>
    public static (bool Ok, string Message, UndoStep? Undo) SetEnabled(StartupItem item, bool enable)
    {
        var hive = item.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;

        try
        {
            using var key = hive.CreateSubKey(item.ApprovalKey, writable: true);
            if (key is null)
                return (false, $"Could not open {item.Hive}\\{item.ApprovalKey}.", null);

            var previous = key.GetValue(item.Name) as byte[];

            var undo = new UndoStep
            {
                Kind = "registry",
                Describe = $"{(enable ? "Disable" : "Re-enable")} \"{item.Name}\" at sign-in",
                Data = new Dictionary<string, string>
                {
                    ["hive"] = item.Hive,
                    ["key"] = item.ApprovalKey,
                    ["name"] = item.Name,
                    ["kind"] = "binary",
                    ["previous"] = Convert.ToBase64String(previous ?? EnabledBlob),
                },
            };

            key.SetValue(item.Name, enable ? EnabledBlob : DisabledBlob, RegistryValueKind.Binary);

            return (true,
                enable
                    ? $"\"{item.Name}\" will start at sign-in again."
                    : $"\"{item.Name}\" will no longer start at sign-in. It is not uninstalled and you can still open it normally.",
                undo);
        }
        catch (UnauthorizedAccessException)
        {
            return (false,
                item.Hive == "HKLM"
                    ? $"Changing \"{item.Name}\" needs administrator rights because it starts for all users."
                    : $"Access was denied changing \"{item.Name}\".",
                null);
        }
        catch (Exception ex)
        {
            return (false, $"Could not change \"{item.Name}\": {ex.Message}", null);
        }
    }
}
