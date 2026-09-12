using System.Net;
using System.Text;
using WinVitals.Core;

namespace WinVitals.Report;

/// <summary>
/// Renders the scan as one self-contained HTML file.
///
/// Self-contained is a requirement, not a convenience: the report's job is to be
/// emailed to a friend, attached to a ticket, or opened on a machine with no
/// network. No CDN, no fonts, no scripts fetched at open time.
/// </summary>
public static class HtmlReport
{
    public static string Render(ScanResult scan, string machineTitle)
    {
        var sb = new StringBuilder();

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>WinVitals report — {E(machineTitle)}</title>");
        sb.Append("<style>").Append(Css).Append("</style>");
        sb.Append("</head><body>");

        Header(sb, scan, machineTitle);
        Summary(sb, scan);
        Attention(sb, scan);
        Modules(sb, scan);
        Footer(sb, scan);

        sb.Append("<script>").Append(Js).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Header(StringBuilder sb, ScanResult scan, string machineTitle)
    {
        sb.Append("<header class=\"top\">");
        sb.Append("<div class=\"brand\">WinVitals</div>");
        sb.Append($"<h1>{E(machineTitle)}</h1>");
        sb.Append("<p class=\"meta\">");
        sb.Append($"Scanned {E(scan.StartedAt.ToString("dddd d MMMM yyyy, HH:mm"))} · ");
        sb.Append($"took {scan.Duration.TotalSeconds:0.#}s · v{E(scan.ToolVersion)}");
        sb.Append("</p>");

        sb.Append("<div class=\"badges\">");
        sb.Append(scan.Elevated
            ? "<span class=\"badge ok\">Full scan (administrator)</span>"
            : "<span class=\"badge warn\">Limited scan — some checks need administrator rights</span>");
        if (scan.Redacted)
            sb.Append("<span class=\"badge info\">Redacted — safe to share</span>");
        sb.Append("</div>");
        sb.Append("</header>");
    }

    private static void Summary(StringBuilder sb, ScanResult scan)
    {
        sb.Append("<section class=\"tiles\">");
        Tile(sb, "critical", scan.Count(Severity.Critical), "Critical");
        Tile(sb, "warning", scan.Count(Severity.Warning), "Warnings");
        Tile(sb, "advisory", scan.Count(Severity.Advisory), "Advisories");
        Tile(sb, "ok", scan.Count(Severity.Ok), "Healthy");
        Tile(sb, "unknown", scan.Count(Severity.Unknown), "Unchecked");
        sb.Append("</section>");
    }

    private static void Tile(StringBuilder sb, string cls, int count, string label)
    {
        sb.Append($"<div class=\"tile {cls}\"><span class=\"n\">{count}</span><span class=\"l\">{E(label)}</span></div>");
    }

    private static void Attention(StringBuilder sb, ScanResult scan)
    {
        var needsAction = scan.AllFindings
            .Where(f => f.Severity is Severity.Critical or Severity.Warning or Severity.Advisory)
            .OrderBy(f => (int)f.Severity)
            .ToList();

        sb.Append("<section class=\"block\">");
        sb.Append("<h2>What needs attention</h2>");

        if (needsAction.Count == 0)
        {
            sb.Append("<p class=\"clean\">Nothing found that needs action. Every check that ran came back healthy.</p>");
            sb.Append("</section>");
            return;
        }

        sb.Append($"<p class=\"lede\">{needsAction.Count} item(s), most urgent first. "
                  + "Each one explains what is true, why it matters, and what to do.</p>");

        foreach (var f in needsAction) FindingCard(sb, f, showModule: true);
        sb.Append("</section>");
    }

    private static void Modules(StringBuilder sb, ScanResult scan)
    {
        sb.Append("<section class=\"block\"><h2>Everything that was checked</h2>");

        foreach (var mod in scan.Modules)
        {
            sb.Append("<details class=\"module\">");
            sb.Append("<summary>");
            sb.Append($"<span class=\"mod-name\">{E(mod.Name)}</span>");
            sb.Append($"<span class=\"mod-counts\">{Counts(mod)}</span>");
            sb.Append("</summary>");
            sb.Append($"<p class=\"blurb\">{E(mod.Blurb)}</p>");

            if (mod.Facts.Count > 0)
            {
                sb.Append("<table class=\"facts\">");
                foreach (var fact in mod.Facts)
                {
                    sb.Append("<tr><th>").Append(E(fact.Label)).Append("</th><td>");
                    sb.Append(E(fact.Value));
                    if (!string.IsNullOrWhiteSpace(fact.Note))
                        sb.Append($" <span class=\"note\">{E(fact.Note!)}</span>");
                    if (!string.IsNullOrWhiteSpace(fact.VerifyAt))
                        sb.Append($" <a class=\"verify\" href=\"{E(fact.VerifyAt!)}\" target=\"_blank\" rel=\"noreferrer\">verify</a>");
                    sb.Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            foreach (var f in mod.Findings.OrderBy(f => (int)f.Severity))
                FindingCard(sb, f, showModule: false);

            sb.Append("</details>");
        }

        sb.Append("</section>");
    }

    private static string Counts(ModuleResult mod)
    {
        var parts = new List<string>();
        foreach (var s in new[] { Severity.Critical, Severity.Warning, Severity.Advisory, Severity.Unknown })
        {
            var n = mod.Findings.Count(f => f.Severity == s);
            if (n > 0) parts.Add($"<em class=\"{Cls(s)}\">{n} {Word(s)}</em>");
        }
        if (parts.Count == 0) parts.Add("<em class=\"ok\">all clear</em>");
        return string.Join(" ", parts);
    }

    private static void FindingCard(StringBuilder sb, Finding f, bool showModule)
    {
        sb.Append($"<article class=\"finding {Cls(f.Severity)}\" id=\"{E(f.Id)}\">");
        sb.Append("<h3>");
        sb.Append($"<span class=\"pill {Cls(f.Severity)}\">{E(Word(f.Severity))}</span>");
        sb.Append(E(f.Title));
        sb.Append("</h3>");

        if (showModule)
            sb.Append($"<p class=\"where\">{E(f.Module)}</p>");

        sb.Append($"<p class=\"what\">{E(f.What)}</p>");

        if (!string.IsNullOrWhiteSpace(f.Why))
            sb.Append($"<p class=\"why\"><span class=\"lbl\">Why this matters</span>{E(f.Why!)}</p>");

        if (!string.IsNullOrWhiteSpace(f.Action))
            sb.Append($"<p class=\"action\"><span class=\"lbl\">What to do</span>{E(f.Action!)}</p>");

        if (!string.IsNullOrWhiteSpace(f.Command))
        {
            sb.Append("<div class=\"cmd\">");
            sb.Append("<button class=\"copy\" type=\"button\">Copy</button>");
            sb.Append($"<pre>{E(f.Command!)}</pre>");
            sb.Append("</div>");
            if (!string.IsNullOrWhiteSpace(f.UndoCommand))
            {
                sb.Append("<details class=\"undo\"><summary>How to undo this</summary>");
                sb.Append($"<pre>{E(f.UndoCommand!)}</pre></details>");
            }
        }

        if (!string.IsNullOrWhiteSpace(f.VerifyAt))
            sb.Append($"<p class=\"verify-line\">Check the current version at "
                      + $"<a href=\"{E(f.VerifyAt!)}\" target=\"_blank\" rel=\"noreferrer\">{E(f.VerifyAt!)}</a></p>");

        if (!string.IsNullOrWhiteSpace(f.Evidence))
        {
            sb.Append("<details class=\"evidence\"><summary>Evidence</summary>");
            sb.Append($"<pre>{E(f.Evidence!)}</pre></details>");
        }

        sb.Append("</article>");
    }

    private static void Footer(StringBuilder sb, ScanResult scan)
    {
        sb.Append("<footer>");
        sb.Append("<p><strong>WinVitals only reads.</strong> It did not change anything on this machine. "
                  + "Every command shown above is for you to run yourself, and each one that changes a setting "
                  + "comes with the command that puts it back.</p>");
        if (scan.Redacted)
            sb.Append("<p>This report was generated with <code>--redact</code>: usernames, machine name, serial "
                      + "number, MAC and IP addresses were replaced with placeholders. It is a best-effort text "
                      + "filter, so give it a skim before posting publicly.</p>");
        else
            sb.Append("<p class=\"warn-text\">This report contains your username, machine name and hardware "
                      + "serial number. Re-run with <code>--redact</code> before posting it in public.</p>");
        sb.Append("</footer>");
    }

    private static string Cls(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "warning",
        Severity.Advisory => "advisory",
        Severity.Ok => "ok",
        _ => "unknown",
    };

    private static string Word(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "warning",
        Severity.Advisory => "advisory",
        Severity.Ok => "healthy",
        _ => "unchecked",
    };

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private const string Js = """
        document.querySelectorAll('.copy').forEach(function (b) {
          b.addEventListener('click', function () {
            var pre = b.parentElement.querySelector('pre');
            navigator.clipboard.writeText(pre.innerText).then(function () {
              b.textContent = 'Copied';
              setTimeout(function () { b.textContent = 'Copy'; }, 1400);
            });
          });
        });
        """;

    private const string Css = """
        :root {
          --bg: #f6f7f9; --card: #ffffff; --ink: #14171a; --muted: #5b6570;
          --line: #e3e7ec; --code-bg: #f0f2f5;
          --critical: #b3261e; --warning: #a05a00; --advisory: #1f5f9e;
          --ok: #1d6f42; --unknown: #6b7280;
          --critical-bg: #fdeceb; --warning-bg: #fdf3e3; --advisory-bg: #eaf2fb;
          --ok-bg: #eaf5ee; --unknown-bg: #f1f2f4;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #14171a; --card: #1c2024; --ink: #e8eaed; --muted: #9aa4af;
            --line: #2c3238; --code-bg: #24292e;
            --critical: #ff8a80; --warning: #ffcc80; --advisory: #8fc3ff;
            --ok: #86e0a8; --unknown: #b0b8c1;
            --critical-bg: #33201e; --warning-bg: #332a1c; --advisory-bg: #1c2836;
            --ok-bg: #1b2c22; --unknown-bg: #23272b;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 0 16px 64px; background: var(--bg); color: var(--ink);
          font: 15px/1.55 -apple-system, "Segoe UI", Roboto, system-ui, sans-serif;
        }
        .top, .tiles, .block, footer { max-width: 900px; margin-inline: auto; }
        .top { padding-block: 32px 20px; }
        .brand {
          font-size: 12px; letter-spacing: .14em; text-transform: uppercase;
          color: var(--muted); font-weight: 700;
        }
        h1 { font-size: 26px; margin: 6px 0 4px; line-height: 1.2; }
        .meta { color: var(--muted); margin: 0 0 12px; font-size: 13px; }
        .badges { display: flex; gap: 8px; flex-wrap: wrap; }
        .badge {
          font-size: 12px; padding: 4px 10px; border-radius: 999px; font-weight: 600;
          background: var(--unknown-bg); color: var(--unknown);
        }
        .badge.ok { background: var(--ok-bg); color: var(--ok); }
        .badge.warn { background: var(--warning-bg); color: var(--warning); }
        .badge.info { background: var(--advisory-bg); color: var(--advisory); }

        .tiles { display: grid; grid-template-columns: repeat(5, 1fr); gap: 10px; margin-bottom: 28px; }
        @media (max-width: 620px) { .tiles { grid-template-columns: repeat(2, 1fr); } }
        .tile {
          background: var(--card); border: 1px solid var(--line); border-radius: 10px;
          padding: 14px 12px; display: flex; flex-direction: column; gap: 2px;
        }
        .tile .n { font-size: 24px; font-weight: 700; font-variant-numeric: tabular-nums; }
        .tile .l { font-size: 12px; color: var(--muted); }
        .tile.critical .n { color: var(--critical); }
        .tile.warning .n { color: var(--warning); }
        .tile.advisory .n { color: var(--advisory); }
        .tile.ok .n { color: var(--ok); }

        h2 { font-size: 19px; margin: 28px 0 6px; }
        .lede, .blurb { color: var(--muted); margin: 0 0 14px; font-size: 14px; }
        .clean {
          background: var(--ok-bg); color: var(--ok); padding: 14px 16px;
          border-radius: 10px; font-weight: 600;
        }

        .finding {
          background: var(--card); border: 1px solid var(--line);
          border-left: 4px solid var(--unknown); border-radius: 10px;
          padding: 16px 18px; margin-bottom: 12px;
        }
        .finding.critical { border-left-color: var(--critical); }
        .finding.warning { border-left-color: var(--warning); }
        .finding.advisory { border-left-color: var(--advisory); }
        .finding.ok { border-left-color: var(--ok); }
        .finding h3 {
          font-size: 16px; margin: 0 0 8px; display: flex; gap: 10px;
          align-items: baseline; flex-wrap: wrap;
        }
        .pill {
          font-size: 10px; letter-spacing: .08em; text-transform: uppercase;
          font-weight: 700; padding: 3px 8px; border-radius: 999px;
          background: var(--unknown-bg); color: var(--unknown); flex: none;
        }
        .pill.critical { background: var(--critical-bg); color: var(--critical); }
        .pill.warning { background: var(--warning-bg); color: var(--warning); }
        .pill.advisory { background: var(--advisory-bg); color: var(--advisory); }
        .pill.ok { background: var(--ok-bg); color: var(--ok); }
        .where { font-size: 12px; color: var(--muted); margin: -4px 0 8px; }
        .what { margin: 0 0 10px; }
        .why, .action { margin: 0 0 10px; font-size: 14px; }
        .lbl {
          display: block; font-size: 11px; text-transform: uppercase;
          letter-spacing: .08em; color: var(--muted); font-weight: 700; margin-bottom: 2px;
        }

        .cmd { position: relative; }
        .cmd pre { margin: 0; }
        pre {
          background: var(--code-bg); border: 1px solid var(--line); border-radius: 8px;
          padding: 12px 14px; overflow-x: auto; font-size: 13px;
          font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
          white-space: pre-wrap; word-break: break-word;
        }
        .copy {
          position: absolute; top: 8px; right: 8px; font-size: 11px; font-weight: 600;
          padding: 4px 10px; border-radius: 6px; border: 1px solid var(--line);
          background: var(--card); color: var(--muted); cursor: pointer;
        }
        .copy:hover { color: var(--ink); }

        details { margin-top: 10px; }
        summary { cursor: pointer; font-size: 13px; color: var(--muted); font-weight: 600; }
        summary:hover { color: var(--ink); }
        .module {
          background: var(--card); border: 1px solid var(--line);
          border-radius: 10px; padding: 14px 18px; margin-bottom: 10px;
        }
        .module > summary {
          display: flex; justify-content: space-between; gap: 12px;
          align-items: baseline; font-size: 15px; color: var(--ink);
        }
        .mod-counts { font-size: 12px; font-style: normal; }
        .mod-counts em { font-style: normal; font-weight: 600; margin-left: 8px; }
        .mod-counts .critical { color: var(--critical); }
        .mod-counts .warning { color: var(--warning); }
        .mod-counts .advisory { color: var(--advisory); }
        .mod-counts .ok { color: var(--ok); }
        .mod-counts .unknown { color: var(--unknown); }

        table.facts { width: 100%; border-collapse: collapse; margin-bottom: 14px; font-size: 14px; }
        table.facts th {
          text-align: left; font-weight: 500; color: var(--muted); padding: 6px 12px 6px 0;
          vertical-align: top; white-space: nowrap; width: 1%;
        }
        table.facts td { padding: 6px 0; vertical-align: top; }
        .note { color: var(--muted); font-size: 13px; }
        .verify, .verify-line a { color: var(--advisory); }
        .verify-line { font-size: 13px; color: var(--muted); }

        footer {
          margin-top: 40px; padding-top: 18px; border-top: 1px solid var(--line);
          font-size: 13px; color: var(--muted);
        }
        footer code { background: var(--code-bg); padding: 1px 5px; border-radius: 4px; }
        .warn-text { color: var(--warning); }
        """;
}
