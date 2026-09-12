namespace WinVitals.Core;

/// <summary>
/// How much the user should care. Ordered most-urgent first so findings sort naturally.
/// </summary>
public enum Severity
{
    /// <summary>Actively harmful or about to be: data loss, no backups, failing disk.</summary>
    Critical = 0,

    /// <summary>Degrades the machine or leaves it exposed, but nothing is on fire.</summary>
    Warning = 1,

    /// <summary>Worth knowing and probably worth doing, at your convenience.</summary>
    Advisory = 2,

    /// <summary>Checked and healthy. Reported so the user can see it was actually looked at.</summary>
    Ok = 3,

    /// <summary>Could not determine — usually needs elevation or the data source was missing.</summary>
    Unknown = 4,
}

/// <summary>
/// One thing WinVitals found and can explain.
///
/// The four-part shape is deliberate and every collector must honour it:
///   What   - what is literally true on this machine, in plain words
///   Why    - why that matters, so the user can decide if they care
///   Action - what to do about it, concretely
///   Evidence - the raw output it was derived from, so nobody has to trust us
///
/// A finding with no <see cref="Action"/> is fine (nothing to do). A finding with
/// no <see cref="Evidence"/> is a bug: we do not make claims we cannot show.
/// </summary>
public sealed record Finding
{
    /// <summary>Stable identifier, e.g. "power.sleep-disabled". Used for --skip and for links.</summary>
    public required string Id { get; init; }

    /// <summary>Module that produced it, e.g. "Power &amp; Sleep".</summary>
    public required string Module { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>One line. No jargon, no vendor names the user has not heard of.</summary>
    public required string Title { get; init; }

    /// <summary>What is actually true on this machine.</summary>
    public required string What { get; init; }

    /// <summary>Why it matters. Skip for Ok findings where the title says it all.</summary>
    public string? Why { get; init; }

    /// <summary>What the user can do. Null means "nothing to do".</summary>
    public string? Action { get; init; }

    /// <summary>
    /// An exact command that performs <see cref="Action"/>. Shown for the user to copy;
    /// WinVitals never runs it. If set, <see cref="Reversible"/> must be answered honestly.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>How to put it back the way it was. Required whenever <see cref="Command"/> changes state.</summary>
    public string? UndoCommand { get; init; }

    /// <summary>Raw data this was derived from.</summary>
    public string? Evidence { get; init; }

    /// <summary>
    /// Where the user should verify a version claim themselves.
    ///
    /// Hard rule: WinVitals never calls a driver, BIOS or app "outdated" or "stale".
    /// It reports the version and date it found and points at the vendor's own release
    /// page. Offline heuristics about what is current are wrong often enough that they
    /// send people chasing updates that do not exist.
    /// </summary>
    public string? VerifyAt { get; init; }

    /// <summary>True when this check was degraded because the scan was not elevated.</summary>
    public bool NeedsElevation { get; init; }
}

/// <summary>A plain inventory value. Facts describe the machine; findings judge it.</summary>
public sealed record Fact
{
    public required string Label { get; init; }
    public required string Value { get; init; }

    /// <summary>Optional context, e.g. "installed 2022-03-14".</summary>
    public string? Note { get; init; }

    /// <summary>Set when the value is a version the user may want to check against the vendor.</summary>
    public string? VerifyAt { get; init; }
}

/// <summary>Everything one collector produced.</summary>
public sealed class ModuleResult
{
    public required string Name { get; init; }
    public required string Blurb { get; init; }
    public List<Fact> Facts { get; } = new();
    public List<Finding> Findings { get; } = new();

    /// <summary>Set when the module could not run at all.</summary>
    public string? Error { get; set; }

    public TimeSpan Duration { get; set; }
}

/// <summary>The whole scan.</summary>
public sealed class ScanResult
{
    public required string ToolVersion { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public bool Elevated { get; init; }
    public bool Redacted { get; init; }
    public List<ModuleResult> Modules { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<Finding> AllFindings => Modules.SelectMany(m => m.Findings);

    public int Count(Severity s) => AllFindings.Count(f => f.Severity == s);
}
