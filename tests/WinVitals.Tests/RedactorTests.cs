using WinVitals.Core;
using Xunit;

namespace WinVitals.Tests;

public class RedactorTests
{
    private static Redactor Make(bool on = true) =>
        new(on, user: "mark", profile: @"C:\Users\mark", machine: "ACMECORP-MARK", domain: "ACMECORP-MARK");

    [Fact]
    public void MachineNameContainingUsernameIsFullyRedacted()
    {
        // Regression: the username rule used to run first, rewriting ACMECORP-MARK to
        // ACMECORP-<user> so the machine rule no longer matched, and the company name
        // survived into a report the user had been told was safe to publish.
        var result = Make().Apply("Report for ACMECORP-MARK");

        Assert.Equal("Report for <machine>", result);
        Assert.DoesNotContain("ACMECORP", result);
    }

    [Fact]
    public void ProfilePathIsRedactedBeforeBareUsername()
    {
        var result = Make().Apply(@"Found at C:\Users\mark\Desktop\thing.txt");

        Assert.Equal(@"Found at C:\Users\<user>\Desktop\thing.txt", result);
    }

    [Fact]
    public void BareUsernameIsStillRedacted()
    {
        Assert.Equal("signed in as <user>", Make().Apply("signed in as mark"));
    }

    [Theory]
    [InlineData("MAC is 3C:52:82:1A:2B:3C", "MAC is <mac>")]
    [InlineData("reached 192.168.1.14 today", "reached <ip> today")]
    [InlineData("mail someone@example.com now", "mail <email> now")]
    public void ShapesThatIdentifyAreReplaced(string input, string expected)
    {
        Assert.Equal(expected, Make().Apply(input));
    }

    [Fact]
    public void SerialKeepsOnlyTheLastFourCharacters()
    {
        Assert.Equal("<serial ending 9XYZ>", Make().Serial("PF3K19XYZ"));
    }

    [Fact]
    public void ShortSerialIsReplacedEntirely()
    {
        Assert.Equal("<serial>", Make().Serial("ABC"));
    }

    [Fact]
    public void DisabledRedactorChangesNothing()
    {
        const string text = @"C:\Users\mark on ACMECORP-MARK";

        Assert.Equal(text, Make(on: false).Apply(text));
        Assert.Equal("PF3K19XYZ", Make(on: false).Serial("PF3K19XYZ"));
    }

    [Fact]
    public void NullIsSafe()
    {
        Assert.Null(Make().Apply(null));
    }
}
