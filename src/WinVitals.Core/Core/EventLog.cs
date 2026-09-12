using System.Diagnostics.Eventing.Reader;

namespace WinVitals.Core;

public sealed record LogEntry(DateTime When, int Id, string Provider, string Message, int Level = 0)
{
    /// <summary>True for events Windows itself classes as an error or worse.</summary>
    public bool IsError => Level is 1 or 2;

    /// <summary>
    /// Event log messages are wrapped and padded for a GUI. Squash them so they read
    /// as a sentence in the report.
    /// </summary>
    public string OneLine =>
        string.Join(" ", Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Pulls a "Label: value" line out of a multi-line event message.</summary>
    public string? Field(string label)
    {
        foreach (var raw in Message.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
                return line[(label.Length + 1)..].Trim().Trim('‎', '‏');
        }
        return null;
    }
}

public static class EventLogReader
{
    /// <summary>
    /// Reads the newest matching events, newest first.
    ///
    /// Returns an empty list rather than throwing: the System log is readable by
    /// standard users on a default install, but group policy, a cleared log, or a
    /// provider that has never fired can each make this fail, and none of those
    /// should take down a scan.
    /// </summary>
    /// <param name="levels">
    /// Windows event levels to keep (1 = critical, 2 = error, 3 = warning, 4 = information).
    /// Filtering on the level rather than on a list of event IDs is the only reliable way
    /// to ask for "errors": several providers use the same ID range for healthy status
    /// notices, and NTFS event 98 — "Volume is healthy, no action is needed" — will
    /// otherwise be counted as a disk error.
    /// </param>
    public static IReadOnlyList<LogEntry> Read(
        string logName, string provider, int[]? ids, int max, TimeSpan? within = null, int[]? levels = null)
    {
        var conditions = new List<string> { $"Provider[@Name='{provider}']" };

        if (ids is { Length: > 0 })
            conditions.Add("(" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")");

        if (levels is { Length: > 0 })
            conditions.Add("(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")");

        // Raw XPath handed to EventLogQuery takes a literal <=, not the XML entity.
        if (within.HasValue)
            conditions.Add($"TimeCreated[timediff(@SystemTime) <= {(long)within.Value.TotalMilliseconds}]");

        var xpath = $"*[System[{string.Join(" and ", conditions)}]]";

        var list = new List<LogEntry>();
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);

            while (list.Count < max)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); }
                catch { break; }
                if (rec is null) break;

                using (rec)
                {
                    string message;
                    try { message = rec.FormatDescription() ?? ""; }
                    catch { message = ""; }

                    list.Add(new LogEntry(
                        rec.TimeCreated?.ToLocalTime() ?? DateTime.MinValue,
                        rec.Id,
                        rec.ProviderName ?? "",
                        message,
                        rec.Level ?? 0));
                }
            }
        }
        catch
        {
            return list;
        }

        return list;
    }
}
