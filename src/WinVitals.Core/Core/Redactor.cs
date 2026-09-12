using System.Text.RegularExpressions;

namespace WinVitals.Core;

/// <summary>
/// Strips machine-identifying strings out of a report so it can be pasted into a
/// forum thread, a ticket, or a chat with someone helping you.
///
/// This exists because the normal end state of a diagnostic report is that a
/// confused person posts the whole thing in public. Tools that do not offer this
/// leak serial numbers and usernames by default.
///
/// It is a best-effort filter over free text, not a security boundary. The report
/// says so, and --redact never claims more than it does.
/// </summary>
public sealed class Redactor
{
    private readonly bool _on;
    private readonly List<(Regex Pattern, string Replacement)> _rules = new();

    public Redactor(bool enabled) : this(
        enabled,
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.MachineName,
        Environment.UserDomainName)
    {
    }

    /// <summary>Identity values are injectable so the ordering rules can be tested.</summary>
    internal Redactor(bool enabled, string user, string profile, string machine, string domain)
    {
        _on = enabled;
        if (!enabled) return;

        // Longest literal first, always.
        //
        // These strings routinely contain one another: a machine called
        // EVENFORCE-MARK belonging to a user called mark. Replace the short one
        // first and the long one is destroyed as a match — EVENFORCE-MARK becomes
        // EVENFORCE-<user>, the machine rule no longer fits, and the company name
        // survives into a report the user was told was safe to publish.
        var literals = new List<(string Value, string Replacement)>
        {
            (profile, @"C:\Users\<user>"),
            (machine, "<machine>"),
            (user, "<user>"),
        };

        if (!string.Equals(domain, machine, StringComparison.OrdinalIgnoreCase))
            literals.Add((domain, "<domain>"));

        foreach (var (value, replacement) in literals.OrderByDescending(l => l.Value?.Length ?? 0))
            AddLiteral(value, replacement);

        // Shapes that identify hardware or the person regardless of this machine.
        _rules.Add((new Regex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b"), "<mac>"));
        _rules.Add((new Regex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b"), "<email>"));
        _rules.Add((new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b"), "<ip>"));
    }

    private void AddLiteral(string value, string replacement)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return;
        _rules.Add((new Regex(Regex.Escape(value), RegexOptions.IgnoreCase), replacement));
    }

    /// <summary>Redact free text. Safe to call on null.</summary>
    public string? Apply(string? text)
    {
        if (!_on || string.IsNullOrEmpty(text)) return text;
        foreach (var (pattern, replacement) in _rules)
            text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>
    /// Hardware serials are not derivable from the environment, so collectors hand
    /// them here explicitly rather than hoping a regex catches them.
    /// </summary>
    public string Serial(string serial)
    {
        if (!_on || string.IsNullOrWhiteSpace(serial)) return serial;
        return serial.Length <= 4 ? "<serial>" : $"<serial ending {serial[^4..]}>";
    }
}
