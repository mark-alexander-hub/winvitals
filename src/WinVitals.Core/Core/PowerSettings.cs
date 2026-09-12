using System.Globalization;
using System.Text.RegularExpressions;

namespace WinVitals.Core;

/// <summary>
/// Reads values out of the active power scheme.
///
/// Shared by the collector that reports a setting and the fix that changes it, so a
/// repair can never disagree with the diagnosis about what the current value is.
/// </summary>
public static class PowerSettings
{
    private static readonly Regex HexIndex = new(@"0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    /// <summary>A setting's AC and DC values. -1 means it could not be read.</summary>
    public readonly record struct Values(int Ac, int Dc)
    {
        public bool Readable => Ac >= 0 || Dc >= 0;
    }

    public static Values Read(string subgroup, string setting)
    {
        var res = Shell.PowerCfg($"/q SCHEME_CURRENT {subgroup} {setting}");
        if (!res.Ok) return new Values(-1, -1);

        var ac = -1;
        var dc = -1;

        foreach (var line in res.Lines)
        {
            var match = HexIndex.Match(line);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                continue;

            if (line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase)) ac = v;
            else if (line.Contains("Current DC Power Setting Index", StringComparison.OrdinalIgnoreCase)) dc = v;
        }

        return new Values(ac, dc);
    }

    /// <summary>Formats a timeout in seconds the way a person would say it.</summary>
    public static string Timeout(int seconds) =>
        seconds < 0 ? "unknown" :
        seconds == 0 ? "Never" :
        seconds % 3600 == 0 ? $"{seconds / 3600} h" :
        seconds >= 3600 ? $"{seconds / 3600} h {(seconds % 3600) / 60} min" :
        $"{seconds / 60} min";
}
