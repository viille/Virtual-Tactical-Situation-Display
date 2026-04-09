using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TacticalDisplay.App.Services;

public sealed class UpdateCheckService
{
    private static readonly Uri LatestReleaseApiUri = new("https://api.github.com/repos/viille/Virtual-Tactical-Situation-Display/releases/latest");
    private static readonly Uri ReleasesPageUri = new("https://github.com/viille/Virtual-Tactical-Situation-Display/releases/latest");

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var currentVersion = GetCurrentVersion();
        if (currentVersion is null)
        {
            return null;
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tactical-Situation-Display/0.5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseApiUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagNameProperty)
            ? tagNameProperty.GetString()
            : null;
        var latestVersion = ParseVersion(tagName);
        if (latestVersion is null || latestVersion <= currentVersion)
        {
            return null;
        }

        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlProperty)
            ? htmlUrlProperty.GetString()
            : null;

        return new UpdateCheckResult(
            currentVersion,
            latestVersion,
            tagName ?? latestVersion.ToString(),
            string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUri : new Uri(htmlUrl));
    }

    public static void OpenReleasesPage(Uri releaseUri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = releaseUri.ToString(),
            UseShellExecute = true
        });
    }

    private static Version? GetCurrentVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return ParseVersion(informationalVersion)
            ?? Assembly.GetExecutingAssembly().GetName().Version;
    }

    private static Version? ParseVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var separatorIndex = normalized.IndexOfAny(['-', '+']);
        if (separatorIndex >= 0)
        {
            normalized = normalized[..separatorIndex];
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }
}

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string LatestTag,
    Uri ReleaseUri);
