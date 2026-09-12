using System.Diagnostics;
using System.Text;

namespace WinVitals.Core;

/// <summary>Result of running an external command.</summary>
public sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>stdout, or stderr when stdout is empty. What you want to show as evidence.</summary>
    public string Text => string.IsNullOrWhiteSpace(StdOut) ? StdErr.Trim() : StdOut.Trim();

    public IEnumerable<string> Lines =>
        Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}

/// <summary>
/// Runs the Windows console tools we lean on (powercfg, dism, vssadmin, ...) and
/// captures their output. Everything here is read-only by contract: if a collector
/// wants to change the machine it must hand the user a command instead.
/// </summary>
public static class Shell
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public static ShellResult Run(string fileName, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return new ShellResult(-1, "", $"could not start {fileName}", false);

            // Read both pipes concurrently. Reading them in sequence deadlocks as soon
            // as either fills its buffer, which powercfg /energy manages easily.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ShellResult(-1, SafeResult(stdout), SafeResult(stderr), true);
            }

            return new ShellResult(p.ExitCode, SafeResult(stdout), SafeResult(stderr), false);
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, "", ex.Message, false);
        }
    }

    private static string SafeResult(Task<string> t)
    {
        try { return t.Wait(TimeSpan.FromSeconds(2)) ? t.Result : ""; }
        catch { return ""; }
    }

    /// <summary>powercfg with the dash form of the flag, which is safe from shell path mangling.</summary>
    public static ShellResult PowerCfg(string args, TimeSpan? timeout = null) =>
        Run("powercfg.exe", args, timeout);
}
