using System.Net.Http;
using System.Text.Json;

namespace WinVitals.App;

/// <summary>
/// Asks GitHub whether a newer release exists. Silent on any failure: offline,
/// rate-limited, no releases yet — none of those are the user's problem.
/// </summary>
public static class UpdateCheck
{
    public sealed record Release(string Tag, string Url);

    public static async Task<Release?> NewerAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WinVitals/{AppInfo.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repository}/releases/latest");
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (!Version.TryParse(AppInfo.Version, out var current)) return null;

            return latest > current ? new Release(tag, url) : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Update check skipped: {ex.Message}");
            return null;
        }
    }
}
