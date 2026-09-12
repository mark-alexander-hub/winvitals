using System.IO;
using System.Text.Json;
using WinVitals.Core;

namespace WinVitals.App;

/// <summary>
/// The boot time on record when the user first changed a startup program.
///
/// This is what makes "Speed up" honest: the next boot after the change can be
/// compared with this one and the difference shown as a number, rather than the
/// page merely asserting that things are faster now.
/// </summary>
public sealed record SpeedupSnapshot(DateTimeOffset TakenAt, DateTime BootAt, int BootMs)
{
    public static SpeedupSnapshot? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SpeedupSnapshot)) return null;
            return JsonSerializer.Deserialize<SpeedupSnapshot>(File.ReadAllText(AppPaths.SpeedupSnapshot));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the speed-up snapshot: {ex.Message}");
            return null;
        }
    }

    /// <summary>Records the latest boot as the "before", unless one is already on record.</summary>
    public static void EnsureTaken()
    {
        if (Load() is not null) return;

        var latest = BootHistory.Read(1).FirstOrDefault();
        if (latest is null) return;

        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var snapshot = new SpeedupSnapshot(DateTimeOffset.Now, latest.When, latest.TotalMs);
            File.WriteAllText(AppPaths.SpeedupSnapshot, JsonSerializer.Serialize(snapshot));
            Log.Info($"Speed-up snapshot taken: boot of {latest.TotalMs} ms at {latest.When}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the speed-up snapshot: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try { File.Delete(AppPaths.SpeedupSnapshot); } catch { /* nothing to clear */ }
    }
}
