using WinVitals.Core;

namespace WinVitals.Remediation;

/// <summary>How much trust a fix asks for.</summary>
public enum FixRisk
{
    /// <summary>Reversible, well understood, cannot break a working machine. Safe to bundle.</summary>
    Safe = 0,

    /// <summary>Reversible but disruptive: needs a restart, takes a long time, or resets configuration.</summary>
    Moderate = 1,

    /// <summary>Changes something the user may depend on, or cannot be undone. Always asked individually.</summary>
    Advanced = 2,
}

/// <summary>
/// One reversal instruction, captured at the moment a change is applied and written
/// to disk before the change happens.
///
/// This is a serialisable record rather than a delegate on purpose. An undo that only
/// exists in memory is worthless: the cases where somebody needs to undo are exactly
/// the cases where the machine has been restarted, or the app has been closed, since
/// the change was made.
/// </summary>
public sealed record UndoStep
{
    /// <summary>Interpreted by <see cref="UndoRunner"/>: "process", "registry", "startup".</summary>
    public required string Kind { get; init; }

    /// <summary>Plain-English description of what undoing this will do.</summary>
    public required string Describe { get; init; }

    public required Dictionary<string, string> Data { get; init; }
}

public sealed record FixResult(
    bool Success,
    string Message,
    IReadOnlyList<UndoStep> Undo,
    bool RestartNeeded = false)
{
    public static FixResult Ok(string message, IReadOnlyList<UndoStep>? undo = null, bool restart = false) =>
        new(true, message, undo ?? Array.Empty<UndoStep>(), restart);

    public static FixResult Failed(string message) =>
        new(false, message, Array.Empty<UndoStep>());
}

public sealed class FixContext
{
    public required bool Elevated { get; init; }
    public Action<string> Progress { get; init; } = _ => { };

    /// <summary>When true, a fix must report what it would do and change nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// A repair for one finding.
///
/// Contract every implementation must honour:
///  - Capture whatever undo information is needed BEFORE making the change.
///  - Return that undo information even on partial success.
///  - If <see cref="Reversible"/> is false, say so plainly in <see cref="Explain"/>;
///    the UI refuses to bundle irreversible fixes into a one-click batch.
/// </summary>
public interface IFix
{
    /// <summary>Id of the finding this repairs. Matched exactly, or as a prefix ending in '.'.</summary>
    string FindingId { get; }

    /// <summary>Other finding ids this same repair also answers.</summary>
    IReadOnlyList<string> AlsoAppliesTo => Array.Empty<string>();

    /// <summary>
    /// True for repairs a user can reach for directly rather than because a finding
    /// pointed at them — "repair Windows system files", "reset the network". These
    /// appear as tools in the UI instead of as a fix attached to a result.
    /// </summary>
    bool Standalone => false;

    /// <summary>Imperative and specific: "Turn automatic sleep back on".</summary>
    string Title { get; }

    /// <summary>What will change, in words a non-technical person can weigh.</summary>
    string Explain { get; }

    FixRisk Risk { get; }
    bool NeedsElevation { get; }
    bool NeedsRestart { get; }

    /// <summary>False when the change cannot be put back, such as deleting files.</summary>
    bool Reversible { get; }

    /// <summary>Exact commands or changes, shown before anything runs.</summary>
    string Preview(Finding finding);

    FixResult Apply(FixContext ctx, Finding finding);
}

/// <summary>Helpers shared by the concrete fixes.</summary>
public static class FixHelpers
{
    /// <summary>Runs a command and turns a non-zero exit into a failed FixResult message.</summary>
    public static (bool Ok, string Output) Run(string file, string args, TimeSpan? timeout = null)
    {
        var res = Shell.Run(file, args, timeout);
        var text = string.IsNullOrWhiteSpace(res.Text) ? "(no output)" : res.Text;
        return (res.Ok, text);
    }

    public static UndoStep Process(string describe, string file, string args) => new()
    {
        Kind = "process",
        Describe = describe,
        Data = new Dictionary<string, string> { ["file"] = file, ["args"] = args },
    };

    /// <summary>
    /// Records a registry value's state before it is changed. A previous value of null
    /// means the value did not exist, and undoing must delete it rather than write a zero.
    /// </summary>
    public static UndoStep Registry(string describe, string hive, string key, string name,
        string? previousValue, string valueKind)
    {
        var data = new Dictionary<string, string>
        {
            ["hive"] = hive,
            ["key"] = key,
            ["name"] = name,
            ["kind"] = valueKind,
        };
        if (previousValue is not null) data["previous"] = previousValue;
        else data["absent"] = "true";

        return new UndoStep { Kind = "registry", Describe = describe, Data = data };
    }
}
