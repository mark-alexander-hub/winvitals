namespace WinVitals.Core;

/// <summary>One recorded boot, from the Diagnostics-Performance log.</summary>
public sealed record BootRecord(DateTime When, int TotalMs, int MainPathMs, int PostBootMs)
{
    public TimeSpan Total => TimeSpan.FromMilliseconds(TotalMs);
}

/// <summary>
/// Reads boot durations Windows already measures on every start.
///
/// Event 100 in Microsoft-Windows-Diagnostics-Performance/Operational carries the
/// numbers as named fields: BootTime is the whole thing, MainPathBootTime is until
/// the desktop appears, BootPostBootTime is the tail where startup programs load.
/// That last one is the part a user can change, and the reason "Speed up" can show
/// a before and after instead of a promise.
/// </summary>
public static class BootHistory
{
    public const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    public const string Provider = "Microsoft-Windows-Diagnostics-Performance";

    /// <summary>Newest first. Empty when the log is unreadable or has never fired.</summary>
    public static IReadOnlyList<BootRecord> Read(int max = 20)
    {
        var entries = EventLogReader.Read(LogName, Provider, new[] { 100 }, max, within: null, levels: null, withData: true);

        var list = new List<BootRecord>();
        foreach (var e in entries)
        {
            var total = e.Number("BootTime");
            if (total is null or <= 0) continue;

            list.Add(new BootRecord(
                e.When,
                (int)total.Value,
                (int)(e.Number("MainPathBootTime") ?? 0),
                (int)(e.Number("BootPostBootTime") ?? 0)));
        }
        return list;
    }

    public static string Seconds(int ms) => $"{ms / 1000.0:0} s";
}
