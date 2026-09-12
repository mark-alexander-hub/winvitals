using WinVitals.Core;

namespace WinVitals.Remediation;

public sealed record AppliedFix(
    string FixId,
    string Title,
    bool Success,
    string Message,
    bool RestartNeeded);

/// <summary>
/// Runs a set of repairs as one supervised operation: safety net first, undo written
/// as it goes, and an honest account at the end.
/// </summary>
public sealed class RepairSession
{
    private readonly bool _elevated;

    public UndoJournal Journal { get; }
    public RestorePointOutcome? SafetyNet { get; private set; }
    public List<AppliedFix> Applied { get; } = new();

    public RepairSession(string toolVersion, bool elevated)
    {
        _elevated = elevated;
        Journal = new UndoJournal(toolVersion);
    }

    /// <summary>
    /// Creates and verifies a restore point. Call once before applying anything.
    ///
    /// The result is deliberately returned rather than thrown or swallowed: if no
    /// restore point could be made, that is the user's decision to weigh, not ours to
    /// hide behind a reassuring message.
    /// </summary>
    public RestorePointOutcome PrepareSafetyNet(string reason)
    {
        SafetyNet = RestorePointGuard.Ensure($"WinVitals — before {reason}", _elevated);
        Journal.Session.RestorePoint = SafetyNet.Created ? SafetyNet.Detail : $"none ({SafetyNet.Summary})";
        Journal.Save();
        return SafetyNet;
    }

    /// <summary>True when this fix can run given the current elevation.</summary>
    public bool CanApply(IFix fix) => !fix.NeedsElevation || _elevated;

    public AppliedFix Apply(IFix fix, Finding finding, Action<string>? progress = null)
    {
        if (!CanApply(fix))
        {
            var blocked = new AppliedFix(fix.FindingId, fix.Title, false,
                "This repair needs administrator rights. Restart WinVitals and accept the prompt.", false);
            Applied.Add(blocked);
            return blocked;
        }

        var ctx = new FixContext
        {
            Elevated = _elevated,
            Progress = progress ?? (_ => { }),
        };

        FixResult result;
        try
        {
            result = fix.Apply(ctx, finding);
        }
        catch (Exception ex)
        {
            result = FixResult.Failed($"The repair stopped with an error: {ex.Message}");
        }

        // Record undo even for a failed fix: a repair that got halfway still changed
        // something, and that half needs to be reversible.
        if (result.Undo.Count > 0)
            Journal.Record(fix.FindingId, fix.Title, result.Undo);

        var applied = new AppliedFix(fix.FindingId, fix.Title, result.Success, result.Message, result.RestartNeeded);
        Applied.Add(applied);
        return applied;
    }

    public bool RestartNeeded => Applied.Any(a => a.Success && a.RestartNeeded);
    public int Succeeded => Applied.Count(a => a.Success);
    public int Failed => Applied.Count(a => !a.Success);
}
