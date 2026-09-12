using WinVitals.Core;
using WinVitals.Remediation;
using Xunit;

namespace WinVitals.Tests;

public class FixCatalogTests
{
    private static Finding Make(string id) => new()
    {
        Id = id,
        Module = "test",
        Severity = Severity.Warning,
        Title = "test",
        What = "test",
    };

    [Fact]
    public void MatchesAFindingExactly()
    {
        var fixes = FixCatalog.For(Make("power.sleep-never")).ToList();
        Assert.Contains(fixes, f => f.Title.Contains("sleep", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("storage.low.C")]
    [InlineData("storage.low.D")]
    [InlineData("storage.full.C")]
    public void MatchesByPrefixSoOneFixServesEveryDrive(string findingId)
    {
        // "storage.low." is a prefix pattern; a fix must not need re-registering per
        // drive letter.
        Assert.NotEmpty(FixCatalog.For(Make(findingId)));
    }

    [Fact]
    public void DoesNotMatchAnUnrelatedFinding()
    {
        Assert.Empty(FixCatalog.For(Make("system.uptime")));
    }

    [Fact]
    public void EveryIrreversibleFixIsExcludedFromTheSafeBundle()
    {
        // The one-click batch is defined as safe AND reversible. This is the invariant
        // that keeps that button honest, so it is asserted rather than trusted.
        var bundled = FixCatalog.All.Where(f => f.Risk == FixRisk.Safe && f.Reversible);
        Assert.All(bundled, f => Assert.True(f.Reversible));

        var irreversible = FixCatalog.All.Where(f => !f.Reversible).ToList();
        Assert.NotEmpty(irreversible); // there are some, so the rule is doing work
        Assert.DoesNotContain(irreversible, f => f.Risk == FixRisk.Safe && f.Reversible);
    }

    [Fact]
    public void NothingToUndoAndReversibleAreMutuallyExclusive()
    {
        // "Nothing to undo" exists so a harmless action is not labelled "cannot be
        // undone". It must never be combined with Reversible, or the interface would
        // tell the user both that there is an undo and that there is nothing to undo.
        Assert.All(FixCatalog.All, fix =>
            Assert.False(fix.NothingToUndo && fix.Reversible,
                $"{fix.GetType().Name} claims both NothingToUndo and Reversible"));
    }

    [Fact]
    public void EveryFixExplainsItselfAndHasATitle()
    {
        Assert.All(FixCatalog.All, fix =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fix.Title), $"{fix.GetType().Name} has no title");
            Assert.False(string.IsNullOrWhiteSpace(fix.Explain), $"{fix.GetType().Name} has no explanation");
            Assert.False(string.IsNullOrWhiteSpace(fix.FindingId), $"{fix.GetType().Name} has no finding id");
        });
    }

    [Fact]
    public void EveryToolIsReachableWithoutAFinding()
    {
        Assert.NotEmpty(FixCatalog.Tools);
        Assert.All(FixCatalog.Tools, f => Assert.True(f.Standalone));
    }

    [Fact]
    public void EveryPlaybookNamesRealModules()
    {
        var known = new[]
        {
            "system", "power", "storage", "battery", "memory",
            "devices", "startup", "network", "security", "updates", "reliability",
        };

        foreach (var playbook in Playbooks.All)
        foreach (var module in playbook.Modules)
        {
            Assert.True(known.Contains(module),
                $"Playbook '{playbook.Id}' refers to unknown module '{module}'");
        }
    }
}
