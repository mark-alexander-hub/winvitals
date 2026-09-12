using System.Diagnostics;
using System.Security.Principal;

namespace WinVitals.Core;

/// <summary>Shared state handed to every collector.</summary>
public sealed class ScanContext
{
    public required bool Elevated { get; init; }
    public required Redactor Redactor { get; init; }

    /// <summary>Called with a short status line so the console can show progress.</summary>
    public Action<string> Progress { get; init; } = _ => { };

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// One area of the machine. Collectors are independent and must not throw: the
/// runner catches anyway, but a collector that gives up early should record what
/// it could not read as an <see cref="Severity.Unknown"/> finding rather than
/// silently reporting a clean bill of health.
/// </summary>
public interface ICollector
{
    /// <summary>Short id used by --only and --skip, e.g. "power".</summary>
    string Id { get; }

    /// <summary>Display name, e.g. "Power &amp; Sleep".</summary>
    string Name { get; }

    /// <summary>One sentence on what this module looks at, shown in the report.</summary>
    string Blurb { get; }

    void Collect(ScanContext ctx, ModuleResult result);
}

/// <summary>Convenience helpers so collectors read declaratively.</summary>
public static class CollectorExtensions
{
    public static void Add(this ModuleResult m, Severity severity, string id, string title,
        string what, string? why = null, string? action = null, string? command = null,
        string? undo = null, string? evidence = null, string? verifyAt = null,
        bool needsElevation = false)
    {
        m.Findings.Add(new Finding
        {
            Id = id,
            Module = m.Name,
            Severity = severity,
            Title = title,
            What = what,
            Why = why,
            Action = action,
            Command = command,
            UndoCommand = undo,
            Evidence = evidence,
            VerifyAt = verifyAt,
            NeedsElevation = needsElevation,
        });
    }

    public static void Ok(this ModuleResult m, string id, string title, string what, string? evidence = null) =>
        m.Add(Severity.Ok, id, title, what, evidence: evidence);

    /// <summary>
    /// Records that a check could not run without admin rights. Never leave the gap
    /// implicit -- an unelevated scan that quietly skips half the power checks is
    /// worse than no scan, because the user believes the machine is clean.
    /// </summary>
    public static void NeedsAdmin(this ModuleResult m, string id, string title, string what) =>
        m.Add(Severity.Unknown, id, title, what,
            why: "This check needs administrator rights and the scan is running as a standard user.",
            action: "Re-run WinVitals and accept the elevation prompt to include this check.",
            needsElevation: true);

    public static void Fact(this ModuleResult m, string label, string value, string? note = null, string? verifyAt = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        m.Facts.Add(new Fact { Label = label, Value = value, Note = note, VerifyAt = verifyAt });
    }

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalMinutes}m";
}

/// <summary>Runs collectors in sequence, timing each and containing their failures.</summary>
public sealed class ScanRunner
{
    private readonly IReadOnlyList<ICollector> _collectors;

    public ScanRunner(IReadOnlyList<ICollector> collectors) => _collectors = collectors;

    public ScanResult Run(ScanContext ctx, string version, bool redacted)
    {
        var result = new ScanResult
        {
            ToolVersion = version,
            Elevated = ctx.Elevated,
            Redacted = redacted,
        };
        var total = Stopwatch.StartNew();

        foreach (var c in _collectors)
        {
            ctx.Progress($"{c.Name}...");
            var module = new ModuleResult { Name = c.Name, Blurb = c.Blurb };
            var sw = Stopwatch.StartNew();
            try
            {
                c.Collect(ctx, module);
            }
            catch (Exception ex)
            {
                module.Error = ex.Message;
                module.Add(Severity.Unknown, $"{c.Id}.failed", $"{c.Name} could not be checked",
                    $"This module stopped with an error: {ex.Message}",
                    why: "The rest of the scan is unaffected, but nothing in this section was verified.",
                    action: "Please open an issue with this message so the check can be made more robust.");
            }
            sw.Stop();
            module.Duration = sw.Elapsed;

            // Redact once, here, so every output format inherits it. Doing this in the
            // renderer would mean the JSON export quietly leaked what the HTML hid.
            Redact(module, ctx.Redactor);
            result.Modules.Add(module);
        }

        total.Stop();
        result.Duration = total.Elapsed;
        return result;
    }

    private static void Redact(ModuleResult module, Redactor r)
    {
        for (var i = 0; i < module.Findings.Count; i++)
        {
            var f = module.Findings[i];
            module.Findings[i] = f with
            {
                Title = r.Apply(f.Title)!,
                What = r.Apply(f.What)!,
                Why = r.Apply(f.Why),
                Action = r.Apply(f.Action),
                Command = r.Apply(f.Command),
                UndoCommand = r.Apply(f.UndoCommand),
                Evidence = r.Apply(f.Evidence),
            };
        }

        for (var i = 0; i < module.Facts.Count; i++)
        {
            var fact = module.Facts[i];
            module.Facts[i] = fact with
            {
                Value = r.Apply(fact.Value)!,
                Note = r.Apply(fact.Note),
            };
        }

        module.Error = r.Apply(module.Error);
    }
}
