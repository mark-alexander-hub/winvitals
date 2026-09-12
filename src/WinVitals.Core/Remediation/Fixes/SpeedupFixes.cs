using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinVitals.Core;

namespace WinVitals.Remediation.Fixes;

/// <summary>Registry helpers for fixes that toggle several values at once.</summary>
internal static class UserSetting
{
    public static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    public static string? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            var v = key?.GetValue(name);
            return v is null ? null : Convert.ToInt32(v).ToString();
        }
        catch { return null; }
    }

    public static bool Write(string path, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, kind);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// Turns off the animations and transparency that cost an old GPU the most.
///
/// Sets the individual switches rather than the "best performance" preset, so the
/// user keeps font smoothing and thumbnails — the two that make Windows look broken
/// when they go.
/// </summary>
public sealed class VisualEffectsForPerformance : IFix
{
    private const string Desktop = @"Control Panel\Desktop";
    private const string Metrics = @"Control Panel\Desktop\WindowMetrics";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Fx = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";

    public string FindingId => "performance.visual-effects";
    public bool Standalone => true;
    public string Title => "Turn off animations and transparency";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => false;
    public bool NeedsRestart => true;
    public bool Reversible => true;

    public string Explain =>
        "Switches off window and taskbar animations, menu fade, Aero Peek and the see-through effects. "
        + "On an older machine these are the difference between a desktop that feels instant and one that "
        + "feels laggy. Text smoothing and icon thumbnails are kept so nothing looks broken. Takes effect "
        + "after you sign out and back in.";

    private static readonly (string Path, string Name, object Value, RegistryValueKind Kind)[] Settings =
    {
        (Fx, "VisualFXSetting", 3, RegistryValueKind.DWord),          // 3 = custom
        (Metrics, "MinAnimate", "0", RegistryValueKind.String),
        (Desktop, "MenuShowDelay", "0", RegistryValueKind.String),
        (Desktop, "DragFullWindows", "0", RegistryValueKind.String),
        (Advanced, "TaskbarAnimations", 0, RegistryValueKind.DWord),
        (Advanced, "ListviewAlphaSelect", 0, RegistryValueKind.DWord),
        (Advanced, "ListviewShadow", 0, RegistryValueKind.DWord),
        (Dwm, "EnableAeroPeek", 0, RegistryValueKind.DWord),
        (@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord),
    };

    public string Preview(Finding finding) =>
        string.Join("\n", Settings.Select(s => $"HKCU\\{s.Path}\\{s.Name} = {s.Value}"));

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok("Would turn off animations and transparency.");

        var undo = new List<UndoStep>();
        var failed = 0;

        foreach (var (path, name, value, kind) in Settings)
        {
            var previous = kind == RegistryValueKind.DWord
                ? UserSetting.ReadDword(path, name)
                : UserSetting.ReadString(path, name);

            undo.Add(FixHelpers.Registry($"Restore {name}", "HKCU", path, name, previous,
                kind == RegistryValueKind.DWord ? "dword" : "string"));

            if (!UserSetting.Write(path, name, value, kind)) failed++;
        }

        if (failed == Settings.Length)
            return new FixResult(false, "None of the settings could be written.", undo);

        var message = "Animations and transparency are off. Sign out and back in to see the difference.";
        if (failed > 0) message += $" {failed} setting(s) could not be written.";
        return new FixResult(true, message, undo, RestartNeeded: true);
    }
}

/// <summary>Switches to the High performance power plan.</summary>
public sealed class HighPerformancePowerPlan : IFix
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private static readonly Regex Guid = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string FindingId => "performance.power-plan";
    public bool Standalone => true;
    public string Title => "Switch to the High performance power plan";
    public FixRisk Risk => FixRisk.Safe;
    public bool NeedsElevation => false;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Explain =>
        "Stops Windows throttling the processor to save power. On a desktop or a plugged-in laptop this "
        + "is free speed. On battery it costs runtime, so switch back to Balanced before unplugging for "
        + "the day — or leave this alone on a laptop you mostly use unplugged.";

    public string Preview(Finding finding)
    {
        var current = Shell.PowerCfg("/getactivescheme").Text;
        return $"Currently: {current.Trim()}\n\npowercfg /setactive {HighPerformance}";
    }

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        var current = Guid.Match(Shell.PowerCfg("/getactivescheme").Text);
        if (ctx.DryRun) return FixResult.Ok("Would activate the High performance plan.");

        var undo = new List<UndoStep>();
        if (current.Success)
            undo.Add(FixHelpers.Process("Switch back to the previous power plan",
                "powercfg.exe", $"/setactive {current.Value}"));

        var res = FixHelpers.Run("powercfg.exe", $"/setactive {HighPerformance}");
        if (!res.Ok)
        {
            // Some OEM images delete the built-in plan. Recreate it from the template.
            var dup = FixHelpers.Run("powercfg.exe", $"/duplicatescheme {HighPerformance}");
            var made = Guid.Match(dup.Output);
            if (made.Success) res = FixHelpers.Run("powercfg.exe", $"/setactive {made.Value}");
        }

        return res.Ok
            ? FixResult.Ok("The High performance plan is active.", undo)
            : new FixResult(false, $"powercfg refused: {res.Output}", undo);
    }
}

/// <summary>Base for fixes that stop and disable a Windows service, with an honest undo.</summary>
public abstract class DisableServiceFix : IFix
{
    protected abstract string Service { get; }
    protected abstract string UndoStartType { get; }

    public abstract string FindingId { get; }
    public abstract string Title { get; }
    public abstract string Explain { get; }

    public bool Standalone => true;
    public FixRisk Risk => FixRisk.Moderate;
    public bool NeedsElevation => true;
    public bool NeedsRestart => false;
    public bool Reversible => true;

    public string Preview(Finding finding) =>
        $"sc stop {Service}\nsc config {Service} start= disabled";

    public FixResult Apply(FixContext ctx, Finding finding)
    {
        if (ctx.DryRun) return FixResult.Ok($"Would stop and disable {Service}.");

        var undo = new List<UndoStep>
        {
            FixHelpers.Process($"Set {Service} back to {UndoStartType}", "sc.exe", $"config {Service} start= {UndoStartType}"),
            FixHelpers.Process($"Start {Service}", "sc.exe", $"start {Service}"),
        };

        ctx.Progress($"Stopping {Service}...");
        FixHelpers.Run("sc.exe", $"stop {Service}", TimeSpan.FromMinutes(1));
        var cfg = FixHelpers.Run("sc.exe", $"config {Service} start= disabled");

        return cfg.Ok
            ? FixResult.Ok($"{Service} is stopped and will not start again.", undo)
            : new FixResult(false, $"Could not disable {Service}: {cfg.Output}", undo);
    }
}

/// <summary>Stops Windows Search indexing, which thrashes a mechanical disk.</summary>
public sealed class DisableSearchIndexing : DisableServiceFix
{
    protected override string Service => "WSearch";
    protected override string UndoStartType => "delayed-auto";

    public override string FindingId => "performance.search-indexing";
    public override string Title => "Stop Windows Search indexing the disk";
    public override string Explain =>
        "Windows Search constantly reads files in the background to keep its index fresh. On a mechanical "
        + "hard disk that is the busy light that never goes off and the reason everything else waits. "
        + "With it off, searching the Start menu still works; searching *inside* documents gets slower. "
        + "Skip this on an SSD — it costs nothing there.";
}

/// <summary>Stops SysMain (Superfetch), which prefetches into memory a slow disk cannot keep up with.</summary>
public sealed class DisableSysMain : DisableServiceFix
{
    protected override string Service => "SysMain";
    protected override string UndoStartType => "auto";

    public override string FindingId => "performance.sysmain";
    public override string Title => "Stop SysMain (Superfetch) pre-loading programs";
    public override string Explain =>
        "SysMain guesses which programs you will open and loads them into memory ahead of time. On a "
        + "machine with a mechanical disk and little memory it is a common cause of 100% disk usage for "
        + "minutes after sign-in. On an SSD with plenty of memory it helps — leave it on there.";
}
