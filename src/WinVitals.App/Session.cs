using WinVitals.Core;

namespace WinVitals.App;

/// <summary>
/// What the interface remembers for the lifetime of the process: the most recent
/// scan, so the dashboard can show a score and results can be revisited without
/// scanning again.
/// </summary>
public static class Session
{
    public static ScanResult? LastScan { get; private set; }
    public static Playbook? LastPlaybook { get; private set; }

    public static void Remember(ScanResult scan, Playbook playbook)
    {
        LastScan = scan;
        LastPlaybook = playbook;
    }

    /// <summary>
    /// A single number for the dashboard. Deliberately blunt: one critical finding
    /// should dominate, and a pile of advisories should not read as a sick machine.
    /// </summary>
    public static int Score(ScanResult scan)
    {
        var score = 100
                    - 25 * scan.Count(Severity.Critical)
                    - 10 * scan.Count(Severity.Warning)
                    - 3 * scan.Count(Severity.Advisory);
        return Math.Clamp(score, 0, 100);
    }

    public static string ScoreWord(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 50 => "Needs attention",
        _ => "Poor",
    };
}
