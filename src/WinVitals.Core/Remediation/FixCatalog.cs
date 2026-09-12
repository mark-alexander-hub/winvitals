using WinVitals.Core;
using WinVitals.Remediation.Fixes;

namespace WinVitals.Remediation;

/// <summary>Every repair WinVitals knows how to perform.</summary>
public static class FixCatalog
{
    public static readonly IReadOnlyList<IFix> All = new IFix[]
    {
        // Power and sleep
        new RestoreSleepTimeouts(),
        new DisableWakeTimers(),
        new DisarmNetworkWake(),
        new EnableHibernate(),

        // Storage
        new CleanTempFiles(),
        new EmptyRecycleBin(),
        new DisableHibernateForSpace(),
        new EnableTrim(),

        // Security
        new TurnOnFirewall(),
        new DisableRemoteDesktop(),
        new UpdateDefenderSignatures(),
        new RemoveRiskyShares(),

        // Windows repair and network
        new RepairSystemFiles(),
        new ResetNetworkStack(),
        new FlushDnsCache(),
        new ResetWindowsUpdateCache(),
    };

    /// <summary>Repairs that answer a given finding.</summary>
    public static IEnumerable<IFix> For(Finding finding) =>
        All.Where(fix => Matches(fix.FindingId, finding.Id)
                         || fix.AlsoAppliesTo.Any(id => Matches(id, finding.Id)));

    /// <summary>Repairs a user can reach for directly, without a finding pointing at them.</summary>
    public static IReadOnlyList<IFix> Tools => All.Where(f => f.Standalone).ToList();

    /// <summary>A pattern ending in '.' matches by prefix, so one fix can serve "storage.low.C" and "storage.low.D".</summary>
    private static bool Matches(string pattern, string findingId) =>
        pattern.EndsWith('.')
            ? findingId.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, findingId, StringComparison.OrdinalIgnoreCase);
}
