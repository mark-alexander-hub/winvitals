using WinVitals.Collectors;
using Xunit;

namespace WinVitals.Tests;

/// <summary>
/// These fixtures are verbatim powercfg output captured from real machines, tabs and
/// all. The formats are undocumented and differ between sleep models, so the parsers
/// are pinned against reality rather than against what the documentation implies.
/// </summary>
public class PowerParsingTests
{
    // Captured from a Lenovo laptop: classic S3 sleep, with the hypervisor holding
    // hybrid sleep because virtualisation-based security is enabled.
    private const string S3Machine =
        "The following sleep states are available on this system:\n" +
        "    Standby (S3)\n" +
        "    Hibernate\n" +
        "    Fast Startup\n" +
        "\n" +
        "The following sleep states are not available on this system:\n" +
        "    Standby (S1)\n" +
        "\tThe system firmware does not support this standby state.\n" +
        "\n" +
        "    Standby (S0 Low Power Idle)\n" +
        "\tThe system firmware does not support this standby state.\n" +
        "\n" +
        "    Hybrid Sleep\n" +
        "\tThe hypervisor does not support this standby state.\n";

    // A Modern Standby machine, where S3 is the state that is missing.
    private const string ModernStandbyMachine =
        "The following sleep states are available on this system:\n" +
        "    Standby (S0 Low Power Idle Network Connected)\n" +
        "    Hibernate\n" +
        "    Fast Startup\n" +
        "\n" +
        "The following sleep states are not available on this system:\n" +
        "    Standby (S1)\n" +
        "\tThe system firmware does not support this standby state.\n" +
        "\n" +
        "    Standby (S3)\n" +
        "\tThe system firmware does not support this standby state.\n";

    [Fact]
    public void S3MachineReportsItsAvailableStates()
    {
        var (available, _) = PowerCollector.ParseAvailability(S3Machine);

        Assert.Contains("Standby (S3)", available);
        Assert.Contains("Hibernate", available);
        Assert.Contains("Fast Startup", available);
        Assert.Equal(3, available.Count);
    }

    [Fact]
    public void ReasonLinesAreAttachedToTheStateTheyExplain()
    {
        var (_, unavailable) = PowerCollector.ParseAvailability(S3Machine);

        var hybrid = Assert.Single(unavailable, u => u.State == "Hybrid Sleep");
        Assert.Contains("hypervisor", hybrid.Reason);

        var s1 = Assert.Single(unavailable, u => u.State == "Standby (S1)");
        Assert.Contains("firmware", s1.Reason);
    }

    [Fact]
    public void ReasonsAreNotMistakenForAvailableStates()
    {
        var (available, _) = PowerCollector.ParseAvailability(S3Machine);

        Assert.DoesNotContain(available, a => a.Contains("does not support"));
    }

    [Fact]
    public void ModernStandbyIsDetectedAndS3IsNot()
    {
        var (available, unavailable) = PowerCollector.ParseAvailability(ModernStandbyMachine);

        Assert.Contains(available, a => a.Contains("S0 Low Power Idle"));
        Assert.Contains(unavailable, u => u.State == "Standby (S3)");
    }

    // ------------------------------------------------------------ requests

    private const string NothingBlocking =
        "DISPLAY:\nNone.\n\n" +
        "SYSTEM:\nNone.\n\n" +
        "AWAYMODE:\nNone.\n\n" +
        "EXECUTION:\nNone.\n\n" +
        "PERFBOOST:\nNone.\n\n" +
        "ACTIVELOCKSCREEN:\nNone.\n";

    private const string AudioDriverHolding =
        "DISPLAY:\nNone.\n\n" +
        "SYSTEM:\n" +
        "[DRIVER] Realtek(R) Audio (INTELAUDIO\\FUNC_01&VEN_10EC&DEV_0257)\n" +
        "An audio stream is currently in use.\n\n" +
        "AWAYMODE:\nNone.\n\n" +
        "EXECUTION:\n" +
        "[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\Some App\\someapp.exe\n\n" +
        "PERFBOOST:\nNone.\n\n" +
        "ACTIVELOCKSCREEN:\nNone.\n";

    [Fact]
    public void NoneMeansNoEntries()
    {
        var sections = PowerCollector.ParseRequests(NothingBlocking);

        Assert.Equal(6, sections.Count);
        Assert.All(sections.Values, entries => Assert.Empty(entries));
    }

    [Fact]
    public void DriverRequestIsCapturedWithItsExplanation()
    {
        var sections = PowerCollector.ParseRequests(AudioDriverHolding);

        var system = Assert.Single(sections["SYSTEM"]);
        Assert.StartsWith("[DRIVER] Realtek(R) Audio", system);

        // The human-readable line underneath belongs to the entry above it, not to a
        // second requester.
        Assert.Contains("An audio stream is currently in use.", system);
    }

    [Fact]
    public void ProcessRequestIsCapturedSeparately()
    {
        var sections = PowerCollector.ParseRequests(AudioDriverHolding);

        var execution = Assert.Single(sections["EXECUTION"]);
        Assert.Contains("someapp.exe", execution);
        Assert.Empty(sections["DISPLAY"]);
    }
}
