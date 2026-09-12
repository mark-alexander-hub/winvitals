using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinVitals.Core;
using WinVitals.Report;

namespace WinVitals.App;

/// <summary>
/// The command-line half of WinVitals: scan, write a report, exit.
///
/// Read-only by design. Repairs are only offered through the interface, where the
/// consequences and the undo can be shown before anything happens.
/// </summary>
public static class Cli
{
    public static int Run(string[] rawArgs)
    {
        var args = Options.Parse(rawArgs);

        if (args.ShowHelp) { PrintHelp(); return 0; }
        if (args.ShowVersion) { Console.WriteLine($"WinVitals {AppInfo.Version}"); return 0; }

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        Banner();

        // The release manifest requires administrator, so this is only ever false for
        // a debug build launched from an unelevated shell. Say so rather than hide it.
        var elevated = ScanContext.IsElevated();
        if (!elevated)
        {
            Console.WriteLine("  Not running as administrator — some checks will be skipped and marked as such.");
            Console.WriteLine();
        }

        var playbook = args.Playbook is null ? Playbooks.Full : Playbooks.ById(args.Playbook);
        if (playbook is null)
        {
            Console.Error.WriteLine($"Unknown check '{args.Playbook}'. Available: "
                                    + string.Join(", ", Playbooks.All.Select(p => p.Id)));
            return 2;
        }

        var collectors = AppInfo.CollectorsFor(playbook);
        if (args.Only.Count > 0)
            collectors = collectors.Where(c => args.Only.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (args.Skip.Count > 0)
            collectors = collectors.Where(c => !args.Skip.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        if (collectors.Count == 0)
        {
            Console.Error.WriteLine("No modules selected. Available: "
                                    + string.Join(", ", AppInfo.AllCollectors.Select(c => c.Id)));
            return 2;
        }

        var ctx = new ScanContext
        {
            Elevated = elevated,
            Redactor = new Redactor(args.Redact),
            Progress = s => Console.Write($"\r  checking {s,-48}"),
        };

        var scan = new ScanRunner(collectors).Run(ctx, AppInfo.Version, args.Redact);
        Console.Write("\r" + new string(' ', 62) + "\r");

        var htmlPath = args.OutPath ?? DefaultPath("html");
        try
        {
            File.WriteAllText(htmlPath, HtmlReport.Render(scan, MachineTitle(ctx.Redactor)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write the report to {htmlPath}: {ex.Message}");
            return 3;
        }

        if (args.JsonPath is not null)
        {
            try { File.WriteAllText(args.JsonPath, JsonSerializer.Serialize(scan, JsonOptions)); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not write JSON: {ex.Message}"); }
        }

        PrintSummary(scan, htmlPath, args);
        if (!args.NoOpen) OpenInBrowser(htmlPath);

        return scan.Count(Severity.Critical) > 0 ? 1 : 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine($"  WinVitals {AppInfo.Version} — read-only check-up");
        Console.WriteLine("  Nothing on this machine will be changed. Run without arguments for the repair interface.");
        Console.WriteLine();
    }

    private static void PrintSummary(ScanResult scan, string htmlPath, Options args)
    {
        var crit = scan.Count(Severity.Critical);
        var warn = scan.Count(Severity.Warning);

        Console.WriteLine($"  Done in {scan.Duration.TotalSeconds:0.#}s.");
        Console.WriteLine();
        Console.WriteLine($"    {crit,3}  critical");
        Console.WriteLine($"    {warn,3}  warnings");
        Console.WriteLine($"    {scan.Count(Severity.Advisory),3}  advisories");
        Console.WriteLine($"    {scan.Count(Severity.Ok),3}  healthy");

        var unknown = scan.Count(Severity.Unknown);
        if (unknown > 0)
            Console.WriteLine($"    {unknown,3}  not checked{(scan.Elevated ? "" : " (needs administrator)")}");
        Console.WriteLine();

        foreach (var f in scan.AllFindings
                     .Where(f => f.Severity is Severity.Critical or Severity.Warning)
                     .OrderBy(f => (int)f.Severity)
                     .Take(5))
        {
            Console.WriteLine($"    • {f.Title}");
        }
        if (crit + warn > 5) Console.WriteLine($"    … and {crit + warn - 5} more");
        Console.WriteLine();

        Console.WriteLine($"  Full report: {htmlPath}");
        if (args.JsonPath is not null) Console.WriteLine($"  JSON:        {args.JsonPath}");
        if (!scan.Redacted)
            Console.WriteLine("  Tip: re-run with --redact before sharing this report with anyone.");
        Console.WriteLine();
    }

    private static string MachineTitle(Redactor redactor)
    {
        var cs = Wmi.First("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        var hardware = string.Join(" ", new[] { cs?.Str("Manufacturer") ?? "", cs?.Str("Model") ?? "" }
            .Where(s => s.Length > 0));
        var name = redactor.Apply(Environment.MachineName) ?? Environment.MachineName;
        return hardware.Length > 0 ? $"{hardware} ({name})" : name;
    }

    private static string DefaultPath(string extension)
    {
        var name = $"WinVitals-report-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(Directory.Exists(desktop) ? desktop : Directory.GetCurrentDirectory(), name);
    }

    private static void OpenInBrowser(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not open the report automatically ({ex.Message}). Open it yourself:");
            Console.WriteLine($"  {path}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            WinVitals {AppInfo.Version} — check-up and repair for a Windows PC.

            Run it with no arguments for the full interface, which can also repair what
            it finds. Run it with arguments for a read-only scan that writes a report.

            USAGE
              WinVitals.exe                    open the interface
              WinVitals.exe [options]          scan and write a report

            OPTIONS
              --check <id>     Run one symptom check instead of everything.
              --out <path>     Where to write the HTML report (default: Desktop).
              --json <path>    Also write the findings as JSON.
              --redact         Replace username, machine name, serial, MAC and IP with
                               placeholders so the report is safe to post publicly.
              --only <ids>     Run only these modules (comma separated).
              --skip <ids>     Run everything except these modules.
              --no-open        Do not open the report when finished.
              --no-elevate     Do not ask for administrator rights.
              --version        Print the version.
              --help           Show this message.

            CHECKS (--check)
              {string.Join("\n  ", Playbooks.All.Select(p => $"{p.Id,-10} {p.Title}"))}

            MODULES (--only / --skip)
              {string.Join("\n  ", AppInfo.AllCollectors.Select(c => $"{c.Id,-10} {c.Blurb}"))}

            EXIT CODES
              0  finished; nothing critical
              1  finished; at least one critical finding
              2  bad arguments
              3  could not write the report
            """);
    }
}

public sealed class Options
{
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool Redact { get; private set; }
    public bool NoOpen { get; private set; }
    public bool NoElevate { get; private set; }
    public string? OutPath { get; private set; }
    public string? JsonPath { get; private set; }
    public string? Playbook { get; private set; }
    public List<string> Only { get; } = new();
    public List<string> Skip { get; } = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
                case "-v" or "--version": o.ShowVersion = true; break;
                case "--redact": o.Redact = true; break;
                case "--no-open": o.NoOpen = true; break;
                case "--no-elevate": o.NoElevate = true; break;
                case "--out": o.OutPath = Next(); break;
                case "--json": o.JsonPath = Next() ?? "winvitals.json"; break;
                case "--check": o.Playbook = Next(); break;
                case "--only": o.Only.AddRange(Split(Next())); break;
                case "--skip": o.Skip.AddRange(Split(Next())); break;
                default:
                    if (a.Length > 0 && !a.StartsWith('-')) o.OutPath ??= a;
                    break;
            }
        }

        return o;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
