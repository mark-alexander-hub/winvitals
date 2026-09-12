using System.Text.RegularExpressions;
using Xunit;

namespace WinVitals.Tests;

/// <summary>
/// The version lives in two places on purpose — the csproj stamps the file's
/// properties, AppInfo drives the update check — and they drifted once. This pins
/// them together.
/// </summary>
public class VersionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.ps1")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [Fact]
    public void CsprojVersionMatchesAppInfoVersion()
    {
        var root = RepoRoot();

        var csproj = File.ReadAllText(Path.Combine(root, "src", "WinVitals.App", "WinVitals.App.csproj"));
        var appInfo = File.ReadAllText(Path.Combine(root, "src", "WinVitals.App", "AppInfo.cs"));

        var projectVersion = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value;
        var codeVersion = Regex.Match(appInfo, @"Version\s*=\s*""([^""]+)""").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(projectVersion), "no <Version> in the csproj");
        Assert.False(string.IsNullOrEmpty(codeVersion), "no Version constant in AppInfo");
        Assert.Equal(projectVersion, codeVersion);
    }

    [Fact]
    public void VersionIsAThreePartNumber()
    {
        var root = RepoRoot();
        var appInfo = File.ReadAllText(Path.Combine(root, "src", "WinVitals.App", "AppInfo.cs"));
        var codeVersion = Regex.Match(appInfo, @"Version\s*=\s*""([^""]+)""").Groups[1].Value;

        // The update check parses tags like v0.4.0 with Version.Parse; a suffix would break it.
        Assert.Matches(@"^\d+\.\d+\.\d+$", codeVersion);
    }
}
