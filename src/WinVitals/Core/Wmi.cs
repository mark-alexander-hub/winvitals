using System.Management;

namespace WinVitals.Core;

/// <summary>
/// Thin wrapper over WMI/CIM. Every call is defensive: on a real fleet of machines,
/// any given class can be missing, blocked by policy, or return properties that are
/// null on one OEM and populated on the next. A collector should never crash a scan
/// because one property was absent.
/// </summary>
public static class Wmi
{
    public static IReadOnlyList<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            using var results = searcher.Get();
            return results.Cast<ManagementObject>().ToList();
        }
        catch
        {
            return Array.Empty<ManagementObject>();
        }
    }

    public static ManagementObject? First(string wql, string scope = @"root\cimv2") =>
        Query(wql, scope).FirstOrDefault();

    public static string Str(this ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    public static long Num(this ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            return v is null ? 0L : Convert.ToInt64(v);
        }
        catch { return 0L; }
    }

    public static bool Flag(this ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            return v is not null && Convert.ToBoolean(v);
        }
        catch { return false; }
    }

    /// <summary>
    /// WMI hands back dates as "20260908143000.000000+330". DateTime.Parse will not
    /// touch that, and ManagementDateTimeConverter throws on the empty string, which
    /// several OEMs cheerfully return for BIOS dates.
    /// </summary>
    public static DateTime? Date(this ManagementBaseObject o, string prop)
    {
        var raw = o.Str(prop);
        if (raw.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw); }
        catch { return null; }
    }
}
