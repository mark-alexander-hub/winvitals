using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinVitals.Collectors;
using WinVitals.Core;
using WinVitals.Report;

namespace WinVitals;

public static class Program
{
    public const string Version = "0.1.0";

    /// <summary>Order here is the order of sections in the report.</summary>
    private static readonly ICollector[] AllCollectors =
    {
        new SystemCollector(),
        new PowerCollector(),
        new StorageCollector(),
        new BatteryCollector(),
        new MemoryCollector(),
        new DeviceCollector(),
        new StartupCollector(),
        new NetworkCollector(),
        new SecurityCollector(),
        new UpdateCollector(),
        new ReliabilityCollector(),
    };

    public static int Main(string[] rawArgs)
    {
        var args = Options.Parse(rawArgs);

        if (args.ShowHelp) { PrintHelp(); return 0; }
        if (args.ShowVersion) { Console.WriteLine($"WinVitals {Version}"); return 0; }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Banner();

        var elevated = ScanContext.IsElevated();

        // Relaunch elevated once, unless told not to. Several of the most useful checks
        // (what is blocking sleep, what is scheduled to wake the machine, disk health)
        // return nothing at all to a standard user, and a scan that silently skips them
        // tells people their machine is fine when it has not been looked at.
        if (!elevated && !args.NoElevate)
        {
            if (TryRelaunchElevated(rawArgs, out var childExit))
                return childExit;

            Console.WriteLine("  Continuing without administrator rights — some checks will be skipped.");
            Console.WriteLine();
        }

        var collectors = SelectCollectors(args);
        if (collectors.Count == 0)
        {
            Console.Error.WriteLine("No modules selected. Available: " +
                string.Join(", ", AllCollectors.Select(c => c.Id)));
            return 2;
        }

        var ctx = new ScanContext
        {
            Elevated = elevated,
            Redactor = new Redactor(args.Redact),
            Progress = s => Console.Write($"\r  checking {s,-48}"),
        };

        var scan = new ScanRunner(collectors).Run(ctx, Version, args.Redact);
        Console.Write("\r" + new string(' ', 62) + "\r");

        var title = MachineTitle(ctx.Redactor);
        var htmlPath = args.OutPath ?? DefaultPath("html");

        try
        {
            File.WriteAllText(htmlPath, HtmlReport.Render(scan, title));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write the report to {htmlPath}: {ex.Message}");
            return 3;
        }

        if (args.JsonPath is not null)
        {
            try
            {
                File.WriteAllText(args.JsonPath, JsonSerializer.Serialize(scan, JsonOptions));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not write JSON to {args.JsonPath}: {ex.Message}");
            }
        }

        PrintSummary(scan, htmlPath, args);

        if (!args.NoOpen) OpenInBrowser(htmlPath);

        // Exit code is useful in scripts: 1 if anything needs attention.
        return scan.Count(Severity.Critical) > 0 ? 1 : 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static List<ICollector> SelectCollectors(Options args)
    {
        IEnumerable<ICollector> set = AllCollectors;
        if (args.Only.Count > 0)
            set = set.Where(c => args.Only.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
        if (args.Skip.Count > 0)
            set = set.Where(c => !args.Skip.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
        return set.ToList();
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine($"  WinVitals {Version} — read-only laptop check-up");
        Console.WriteLine("  Nothing on this machine will be changed.");
        Console.WriteLine();
    }

    private static void PrintSummary(ScanResult scan, string htmlPath, Options args)
    {
        var crit = scan.Count(Severity.Critical);
        var warn = scan.Count(Severity.Warning);
        var adv = scan.Count(Severity.Advisory);
        var ok = scan.Count(Severity.Ok);
        var unk = scan.Count(Severity.Unknown);

        Console.WriteLine($"  Done in {scan.Duration.TotalSeconds:0.#}s.");
        Console.WriteLine();
        Console.WriteLine($"    {crit,3}  critical");
        Console.WriteLine($"    {warn,3}  warnings");
        Console.WriteLine($"    {adv,3}  advisories");
        Console.WriteLine($"    {ok,3}  healthy");
        if (unk > 0) Console.WriteLine($"    {unk,3}  not checked{(scan.Elevated ? "" : " (needs administrator)")}");
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
        var maker = cs?.Str("Manufacturer") ?? "";
        var model = cs?.Str("Model") ?? "";
        var name = redactor.Apply(Environment.MachineName) ?? Environment.MachineName;

        var hardware = string.Join(" ", new[] { maker, model }.Where(s => s.Length > 0));
        return hardware.Length > 0 ? $"{hardware} ({name})" : name;
    }

    private static string DefaultPath(string extension)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var name = $"WinVitals-report-{stamp}.{extension}";

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dir = Directory.Exists(desktop) ? desktop : Directory.GetCurrentDirectory();
        return Path.Combine(dir, name);
    }

    private static bool TryRelaunchElevated(string[] rawArgs, out int exitCode)
    {
        exitCode = 0;
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        Console.WriteLine("  Some checks need administrator rights. Asking for elevation...");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", rawArgs.Select(Quote).Append("--no-elevate")),
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            exitCode = p.ExitCode;
            return true;
        }
        catch (Exception)
        {
            // Almost always the user clicking No on the UAC prompt. That is a valid
            // choice, so fall through to a reduced scan rather than refusing to run.
            Console.WriteLine("  Elevation declined.");
            return false;
        }
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static void OpenInBrowser(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not open the report automatically ({ex.Message}). Open it yourself:");
            Console.WriteLine($"  {path}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            WinVitals {Version} — a read-only check-up for a Windows laptop.

            It looks at power and sleep, storage, battery, startup, drivers, security
            and reliability, then writes one self-contained HTML report explaining what
            it found, why each thing matters, and what you can do about it.

            WinVitals never changes anything. Where a fix exists it shows you the exact
            command, plus the command that undoes it.

            USAGE
              WinVitals.exe [options]

            OPTIONS
              --out <path>     Where to write the HTML report.
                               Default: a timestamped file on your Desktop.
              --json <path>    Also write the findings as JSON.
              --redact         Replace username, machine name, serial, MAC and IP with
                               placeholders so the report is safe to post publicly.
              --only <ids>     Run only these modules (comma separated).
              --skip <ids>     Run everything except these modules.
              --no-open        Do not open the report in a browser when finished.
              --no-elevate     Do not ask for administrator rights. Some checks will
                               be skipped and the report will say which.
              --version        Print the version and exit.
              --help           Show this message.

            MODULES
              {string.Join("\n  ", AllCollectors.Select(c => $"{c.Id,-10} {c.Blurb}"))}

            EXIT CODES
              0  finished; nothing critical
              1  finished; at least one critical finding
              2  bad arguments
              3  could not write the report
            """);
    }
}

/// <summary>Command line options. Kept deliberately small and forgiving.</summary>
public sealed class Options
{
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool Redact { get; private set; }
    public bool NoOpen { get; private set; }
    public bool NoElevate { get; private set; }
    public string? OutPath { get; private set; }
    public string? JsonPath { get; private set; }
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
                case "--only": o.Only.AddRange(Split(Next())); break;
                case "--skip": o.Skip.AddRange(Split(Next())); break;
                default:
                    if (a.Length > 0 && !a.StartsWith('-'))
                        o.OutPath ??= a; // bare path is treated as --out
                    break;
            }
        }

        return o;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
