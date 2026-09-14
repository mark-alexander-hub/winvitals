using WinVitals.Remediation;
using Xunit;

namespace WinVitals.Tests;

/// <summary>
/// The guard suspends the 24-hour restore point throttle by overwriting a machine-wide
/// registry value, and notes the original on disk so a run that is killed before its
/// finally block can be undone by the next one. That note is the only record of what
/// the machine looked like beforehand, so how it is read matters.
///
/// This is not hypothetical: on 14 September a killed run left the throttle at 0, and
/// the run after it read that 0 as the machine's own setting and restored nothing.
/// </summary>
public class RestorePointGuardTests
{
    [Theory]
    [InlineData("1440", 1440)]
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    public void ReadsBackARecordedThrottleValue(string note, int expected)
    {
        Assert.Equal(expected, RestorePointGuard.ParseState(note));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("corrupted")]
    public void TreatsAnythingUnrecognisedAsAbsent(string note)
    {
        // Null means "the value did not exist", which tells the guard to delete it
        // rather than invent one. Guessing a number here would leave the machine in a
        // state it was never in.
        Assert.Null(RestorePointGuard.ParseState(note));
    }

    [Fact]
    public void TheAbsentMarkerItselfRoundTrips()
    {
        Assert.Null(RestorePointGuard.ParseState(RestorePointGuard.AbsentMarker));
    }
}
