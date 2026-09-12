using Microsoft.Win32;
using WinVitals.Remediation;
using Xunit;

namespace WinVitals.Tests;

/// <summary>
/// Exercises the undo mechanism against a scratch key under HKCU.
///
/// The whole tool rests on this working: WinVitals is only safe to let near a machine
/// because anything it changes can be put back. These tests perform real registry
/// writes and real reversals rather than asserting on a mock.
/// </summary>
public sealed class UndoTests : IDisposable
{
    private const string TestKey = @"Software\WinVitalsTests";
    private const string ValueName = "Sample";

    public UndoTests() => Cleanup();
    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(TestKey, throwOnMissingSubKey: false); }
        catch { /* nothing to clean */ }
    }

    private static UndoEntry Entry(params UndoStep[] steps) => new()
    {
        FixId = "test",
        Title = "test change",
        When = DateTimeOffset.Now,
        Steps = steps.ToList(),
    };

    [Fact]
    public void RestoresAPreviousDwordValue()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, 7, RegistryValueKind.DWord);

        // Captured the way a fix captures it: before the change.
        var undo = FixHelpers.Registry("Put it back", "HKCU", TestKey, ValueName, "7", "dword");

        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, 99, RegistryValueKind.DWord);

        var (ok, _) = UndoRunner.Undo(Entry(undo));

        Assert.True(ok);
        using var check = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Equal(7, check.GetValue(ValueName));
    }

    [Fact]
    public void DeletesAValueThatDidNotExistBefore()
    {
        // The important case. A value that was absent must be removed on undo, not set
        // to zero: writing a zero where Windows expected nothing is a different state,
        // and for a flag like fDenyTSConnections it is the opposite of the original.
        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
        {
            // Key exists, value does not.
            key.SetValue("Unrelated", 1, RegistryValueKind.DWord);
        }

        var undo = FixHelpers.Registry("Remove it", "HKCU", TestKey, ValueName, previousValue: null, "dword");

        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, 1, RegistryValueKind.DWord);

        var (ok, _) = UndoRunner.Undo(Entry(undo));

        Assert.True(ok);
        using var check = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Null(check.GetValue(ValueName));
        Assert.Equal(1, check.GetValue("Unrelated"));
    }

    [Fact]
    public void RestoresBinaryValuesSuchAsStartupFlags()
    {
        var original = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, original, RegistryValueKind.Binary);

        var undo = FixHelpers.Registry("Re-enable at sign-in", "HKCU", TestKey, ValueName,
            Convert.ToBase64String(original), "binary");

        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        var (ok, _) = UndoRunner.Undo(Entry(undo));

        Assert.True(ok);
        using var check = Registry.CurrentUser.OpenSubKey(TestKey)!;
        Assert.Equal(original, (byte[])check.GetValue(ValueName)!);
    }

    [Fact]
    public void StepsAreReversedInReverseOrder()
    {
        // A fix that changed several things in sequence has to be unwound like a stack.
        using (var key = Registry.CurrentUser.CreateSubKey(TestKey)!)
            key.SetValue(ValueName, 1, RegistryValueKind.DWord);

        var first = FixHelpers.Registry("set to 1", "HKCU", TestKey, ValueName, "1", "dword");
        var second = FixHelpers.Registry("set to 2", "HKCU", TestKey, ValueName, "2", "dword");

        var (ok, _) = UndoRunner.Undo(Entry(first, second));

        Assert.True(ok);
        using var check = Registry.CurrentUser.OpenSubKey(TestKey)!;
        // Reversed order means "second" runs first and "first" runs last, so 1 wins.
        Assert.Equal(1, check.GetValue(ValueName));
    }

    [Fact]
    public void MarksTheEntryUndoneOnlyWhenEverythingSucceeded()
    {
        var bad = new UndoStep
        {
            Kind = "not-a-real-kind",
            Describe = "nonsense",
            Data = new Dictionary<string, string>(),
        };

        var entry = Entry(bad);
        var (ok, messages) = UndoRunner.Undo(entry);

        Assert.False(ok);
        Assert.False(entry.Undone);
        Assert.Contains(messages, m => m.Contains("not-a-real-kind"));
    }

    [Fact]
    public void JournalRoundTripsThroughDisk()
    {
        var journal = new UndoJournal("test-version");
        journal.Record("power.sleep-never", "Turn automatic sleep back on", new List<UndoStep>
        {
            FixHelpers.Process("Set it back", "powercfg.exe", "/change standby-timeout-ac 0"),
        });

        var reloaded = UndoJournal.LoadAll().FirstOrDefault(s => s.Session.Id == journal.Session.Id);

        Assert.NotEqual(default, reloaded);
        var entry = Assert.Single(reloaded.Session.Entries);
        Assert.Equal("Turn automatic sleep back on", entry.Title);

        var step = Assert.Single(entry.Steps);
        Assert.Equal("process", step.Kind);
        Assert.Equal("powercfg.exe", step.Data["file"]);

        try { File.Delete(reloaded.Path); } catch { /* best effort */ }
    }
}
