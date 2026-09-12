using System.Text.Json;
using Microsoft.Win32;

namespace WinVitals.Remediation;

/// <summary>One fix that was applied, and how to reverse it.</summary>
public sealed record UndoEntry
{
    public required string FixId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset When { get; init; }
    public required List<UndoStep> Steps { get; init; }
    public bool Undone { get; set; }
}

/// <summary>Everything applied in one sitting.</summary>
public sealed record UndoSession
{
    public required string Id { get; init; }
    public required DateTimeOffset Started { get; init; }
    public required string ToolVersion { get; init; }
    public string? RestorePoint { get; set; }
    public List<UndoEntry> Entries { get; init; } = new();
}

/// <summary>
/// Persists undo information to disk as it is created.
///
/// Written before each change lands, not after the batch finishes. If the machine
/// loses power halfway through a set of fixes, the record of what had already been
/// changed has to survive that.
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinVitals", "undo");

    private readonly string _path;
    public UndoSession Session { get; }

    public UndoJournal(string toolVersion)
    {
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Session = new UndoSession { Id = id, Started = DateTimeOffset.Now, ToolVersion = toolVersion };

        System.IO.Directory.CreateDirectory(Directory);
        _path = Path.Combine(Directory, $"{id}.json");
    }

    public void Record(string fixId, string title, IReadOnlyList<UndoStep> steps)
    {
        if (steps.Count == 0) return;

        Session.Entries.Add(new UndoEntry
        {
            FixId = fixId,
            Title = title,
            When = DateTimeOffset.Now,
            Steps = steps.ToList(),
        });
        Save();
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Session, Json)); }
        catch { /* an unwritable journal must not stop a repair the user asked for */ }
    }

    /// <summary>Writes a session loaded by <see cref="LoadAll"/> back to its file.</summary>
    public static bool SaveTo(string path, UndoSession session)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(session, Json));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every recorded session, newest first.</summary>
    public static IReadOnlyList<(string Path, UndoSession Session)> LoadAll()
    {
        var list = new List<(string, UndoSession)>();
        if (!System.IO.Directory.Exists(Directory)) return list;

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var session = JsonSerializer.Deserialize<UndoSession>(File.ReadAllText(file));
                if (session is not null) list.Add((file, session));
            }
            catch { /* skip a corrupt journal rather than failing the whole list */ }
        }

        return list.OrderByDescending(x => x.Item2.Started).ToList();
    }
}

/// <summary>Replays undo steps.</summary>
public static class UndoRunner
{
    /// <summary>
    /// Reverses one entry. Steps run in reverse order, because a fix that changed
    /// several things in sequence has to be unwound the way you unwind a stack.
    /// </summary>
    public static (bool Ok, List<string> Messages) Undo(UndoEntry entry)
    {
        var messages = new List<string>();
        var ok = true;

        foreach (var step in Enumerable.Reverse(entry.Steps))
        {
            try
            {
                var result = step.Kind switch
                {
                    "process" => UndoProcess(step),
                    "registry" => UndoRegistry(step),
                    _ => (false, $"Do not know how to undo a step of kind '{step.Kind}'."),
                };

                ok &= result.Item1;
                messages.Add(result.Item2);
            }
            catch (Exception ex)
            {
                ok = false;
                messages.Add($"{step.Describe}: {ex.Message}");
            }
        }

        entry.Undone = ok;
        return (ok, messages);
    }

    private static (bool, string) UndoProcess(UndoStep step)
    {
        var file = step.Data.GetValueOrDefault("file", "");
        var args = step.Data.GetValueOrDefault("args", "");
        if (file.Length == 0) return (false, "Undo step has no command recorded.");

        var res = Core.Shell.Run(file, args);
        return res.Ok
            ? (true, step.Describe)
            : (false, $"{step.Describe} — failed: {res.Text}");
    }

    private static (bool, string) UndoRegistry(UndoStep step)
    {
        var hiveName = step.Data.GetValueOrDefault("hive", "HKLM");
        var keyPath = step.Data.GetValueOrDefault("key", "");
        var name = step.Data.GetValueOrDefault("name", "");
        var kind = step.Data.GetValueOrDefault("kind", "dword");

        var baseKey = hiveName.ToUpperInvariant() switch
        {
            "HKCU" => Microsoft.Win32.Registry.CurrentUser,
            "HKLM" => Microsoft.Win32.Registry.LocalMachine,
            _ => null,
        };
        if (baseKey is null || keyPath.Length == 0)
            return (false, $"Undo step has an unusable registry path ({hiveName}\\{keyPath}).");

        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        if (key is null) return (false, $"Registry key {hiveName}\\{keyPath} no longer exists.");

        // A value that did not exist before must be deleted, not set to zero. Writing a
        // zero where Windows expected nothing is a different state from the original.
        if (step.Data.ContainsKey("absent"))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return (true, $"{step.Describe} (removed the value, which did not exist before)");
        }

        var previous = step.Data.GetValueOrDefault("previous", "");
        switch (kind)
        {
            case "dword":
                if (!int.TryParse(previous, out var number))
                    return (false, $"Recorded previous value '{previous}' is not a number.");
                key.SetValue(name, number, RegistryValueKind.DWord);
                break;

            case "binary":
                key.SetValue(name, Convert.FromBase64String(previous), RegistryValueKind.Binary);
                break;

            default:
                key.SetValue(name, previous, RegistryValueKind.String);
                break;
        }

        return (true, step.Describe);
    }
}
