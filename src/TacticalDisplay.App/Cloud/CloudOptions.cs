using System.IO;
using System.Text.Json;

namespace TacticalDisplay.App.Cloud;

public sealed class CloudOptions
{
    private const string DefaultUrl = "https://www.vtsd.app";
    public Uri BaseUri { get; init; } = new("https://www.vtsd.app");
    public Uri DashboardUri { get; init; } = new("https://www.vtsd.app/dashboard/");

    public static CloudOptions Load()
    {
        var configuredBase = ReadSettings()?.VtsdCloud?.BaseUrl;
        var configuredDashboard = ReadSettings()?.VtsdCloud?.DashboardUrl;
        var baseUrl = Environment.GetEnvironmentVariable("VTSD_CLOUD_URL") ?? configuredBase ?? DefaultUrl;
        var dashboardUrl = Environment.GetEnvironmentVariable("VTSD_DASHBOARD_URL") ?? configuredDashboard ?? (baseUrl.TrimEnd('/') + "/dashboard/");
        return new CloudOptions
        {
            BaseUri = RequireHttps(baseUrl, "VTSD Cloud"),
            DashboardUri = RequireHttps(dashboardUrl, "VTSD Dashboard")
        };
    }

    private static SettingsRoot? ReadSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<SettingsRoot>(File.ReadAllText(path), JsonOptions.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static Uri RequireHttps(string? value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{name} URL must be an absolute HTTPS URL.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private sealed class SettingsRoot { public CloudSection? VtsdCloud { get; set; } }
    private sealed class CloudSection { public string? BaseUrl { get; set; } public string? DashboardUrl { get; set; } }
}
