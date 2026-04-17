using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace TacticalDisplay.App.Services;

public sealed class UpdateCheckService
{
    private const string AppAssetName = "TacticalDisplay.App.exe";
    private const string EmbeddedUpdaterResourceName = "tacticaldisplay.updater.exe";
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tactical-Situation-Display/0.11.1");
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
        var assetDownloadUrl = ResolveAppAssetDownloadUrl(root);
        var releaseNotes = root.TryGetProperty("body", out var bodyProperty)
            ? bodyProperty.GetString()
            : null;

        return new UpdateCheckResult(
            currentVersion,
            latestVersion,
            tagName ?? latestVersion.ToString(),
            string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUri : new Uri(htmlUrl),
            assetDownloadUrl,
            releaseNotes);
    }

    public async Task<bool> DownloadAndStartUpdateAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        if (result.AssetDownloadUri is null)
        {
            return false;
        }

        var currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
        {
            return false;
        }

        var updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "VirtualTacticalSituationDisplay",
            "updates",
            result.LatestTag,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);

        var newExePath = Path.Combine(updateDirectory, AppAssetName);
        var updaterExePath = Path.Combine(updateDirectory, "TacticalDisplay.Updater.exe");

        await DownloadFileAsync(result.AssetDownloadUri, newExePath, cancellationToken);
        if (!TryExtractUpdater(updaterExePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = updaterExePath,
            WorkingDirectory = updateDirectory,
            UseShellExecute = true
        }
        .WithArguments(
            Environment.ProcessId.ToString(),
            newExePath,
            currentExePath));

        return true;
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

    private static Uri? ResolveAppAssetDownloadUrl(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            if (!string.Equals(name, AppAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var downloadUrlProperty)
                ? downloadUrlProperty.GetString()
                : null;
            return string.IsNullOrWhiteSpace(downloadUrl) ? null : new Uri(downloadUrl);
        }

        return null;
    }

    private static async Task DownloadFileAsync(Uri uri, string targetPath, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tactical-Situation-Display/0.11.1");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool TryExtractUpdater(string targetPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedUpdaterResourceName);
        if (stream is not null)
        {
            using var manifestTarget = File.Create(targetPath);
            stream.CopyTo(manifestTarget);
            return true;
        }

        var resource = Application.GetResourceStream(new Uri(EmbeddedUpdaterResourceName, UriKind.Relative));
        if (resource is null)
        {
            return false;
        }

        using var target = File.Create(targetPath);
        resource.Stream.CopyTo(target);
        return true;
    }
}

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string LatestTag,
    Uri ReleaseUri,
    Uri? AssetDownloadUri,
    string? ReleaseNotes);

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
