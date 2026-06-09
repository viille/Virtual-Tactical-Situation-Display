using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.IO;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class TelemetryService
{
    private const string EndpointUrl = "https://vtsd-telemetry.vercel.app/api/telemetry";
    private const string IngestKeyHeaderName = "X-VTSD-Ingest-Key";
    private const string IngestKeyMetadataKey = "DefaultTelemetryIngestKey";
    private const string IngestKeyEnvironmentVariable = "VTSD_INGEST_KEY";
    private const string AppActiveEventName = "app_active";
    private const string DiagnosticSnapshotEventName = "diagnostic_snapshot";
    private const string AppVersionChangedEventName = "app_version_changed";
    private const string UtcDateFormat = "yyyy-MM-dd";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly Func<HttpClient> _httpClientFactory;

    public TelemetryService()
        : this(
            Path.Combine(AppDataPaths.ApplicationDataDirectory, "telemetry.json"),
            static () => new HttpClient { Timeout = RequestTimeout })
    {
    }

    internal TelemetryService(string statePath, Func<HttpClient> httpClientFactory)
    {
        _statePath = statePath;
        _httpClientFactory = httpClientFactory;
    }

    public void SendStartupTelemetryInBackground(
        string appVersion,
        TacticalDisplaySettings settings,
        bool includeDiagnostics)
    {
        var diagnostics = includeDiagnostics ? TelemetryDiagnostics.FromSettings(settings) : null;
        _ = Task.Run(() => SendStartupTelemetryAsync(appVersion, diagnostics, CancellationToken.None));
    }

    public string GetOrCreateInstallationId()
    {
        var stateFileExists = File.Exists(_statePath);
        var state = LoadState();
        if (!stateFileExists || string.IsNullOrWhiteSpace(state.InstallationId))
        {
            SaveState(state);
        }

        return state.InstallationId!;
    }

    public string GetConfiguredIngestKey() => ResolveIngestKey();

    internal async Task SendStartupTelemetryAsync(
        string appVersion,
        TelemetryDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        var today = DateTimeOffset.UtcNow.ToString(UtcDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        var stateFileExists = File.Exists(_statePath);
        var state = LoadState();
        if (!stateFileExists)
        {
            SaveState(state);
        }

        var normalizedAppVersion = NormalizeAppVersion(appVersion);
        var ingestKey = ResolveIngestKey();
        if (string.IsNullOrWhiteSpace(ingestKey))
        {
            DataSourceDebugLog.Debug("Telemetry", "Telemetry ingest key is not configured; skipping app_active ping");
            return;
        }

        if (string.IsNullOrWhiteSpace(state.InstallationId))
        {
            state = state with { InstallationId = Guid.NewGuid().ToString("D") };
            SaveState(state);
        }

        using var client = _httpClientFactory();
        if (string.IsNullOrWhiteSpace(state.LastReportedAppVersion) && !stateFileExists)
        {
            state = state with { LastReportedAppVersion = normalizedAppVersion };
            SaveState(state);
        }
        else if ((string.IsNullOrWhiteSpace(state.LastReportedAppVersion) ||
            !string.Equals(state.LastReportedAppVersion, normalizedAppVersion, StringComparison.Ordinal)) &&
            await SendTelemetryRequestAsync(
                client,
                ingestKey,
                new TelemetryRequest(
                    state.InstallationId,
                    normalizedAppVersion,
                    AppVersionChangedEventName,
                    null),
                cancellationToken).ConfigureAwait(false))
        {
            state = state with { LastReportedAppVersion = normalizedAppVersion };
            SaveState(state);
            DataSourceDebugLog.Debug("Telemetry", "Telemetry app_version_changed ping sent");
        }

        if (!string.Equals(state.LastSentUtcDate, today, StringComparison.Ordinal) &&
            await SendTelemetryRequestAsync(
                client,
                ingestKey,
                new TelemetryRequest(
                    state.InstallationId,
                    normalizedAppVersion,
                    AppActiveEventName,
                    null),
                cancellationToken).ConfigureAwait(false))
        {
            state = state with { LastSentUtcDate = today };
            SaveState(state);
            DataSourceDebugLog.Debug("Telemetry", "Telemetry app_active ping sent");
        }

        if (diagnostics is not null &&
            !string.Equals(state.LastDiagnosticsSentUtcDate, today, StringComparison.Ordinal) &&
            await SendTelemetryRequestAsync(
                client,
                ingestKey,
                new TelemetryRequest(
                    state.InstallationId,
                    normalizedAppVersion,
                    DiagnosticSnapshotEventName,
                    diagnostics),
                cancellationToken).ConfigureAwait(false))
        {
            SaveState(state with { LastDiagnosticsSentUtcDate = today });
            DataSourceDebugLog.Debug("Telemetry", "Telemetry diagnostic_snapshot sent");
        }
    }

    private static async Task<bool> SendTelemetryRequestAsync(
        HttpClient client,
        string ingestKey,
        TelemetryRequest telemetryRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            request.Headers.Add(IngestKeyHeaderName, ingestKey.Trim());
            request.Content = JsonContent.Create(telemetryRequest, options: JsonOptions);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DataSourceDebugLog.Debug("Telemetry", $"Telemetry request failed | event={telemetryRequest.Event} status={(int)response.StatusCode}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry request unavailable | event={telemetryRequest.Event} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry request skipped | event={telemetryRequest.Event} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private TelemetryState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return new TelemetryState(Guid.NewGuid().ToString("D"), null);
            }

            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize<TelemetryState>(json, JsonOptions) ?? new TelemetryState(null, null);
            return string.IsNullOrWhiteSpace(state.InstallationId)
                ? state with { InstallationId = Guid.NewGuid().ToString("D") }
                : state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry state unavailable; using a fresh anonymous id | {ex.GetType().Name}: {ex.Message}");
            return new TelemetryState(Guid.NewGuid().ToString("D"), null);
        }
    }

    private void SaveState(TelemetryState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry state save failed | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string NormalizeAppVersion(string appVersion)
    {
        var trimmed = appVersion.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "unknown" : trimmed[..Math.Min(trimmed.Length, 32)];
    }

    private static string ResolveIngestKey()
    {
        var metadataValue = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, IngestKeyMetadataKey, StringComparison.Ordinal))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadataValue))
        {
            return metadataValue;
        }

        return Environment.GetEnvironmentVariable(IngestKeyEnvironmentVariable) ?? string.Empty;
    }

    private sealed record TelemetryState(
        string? InstallationId,
        string? LastSentUtcDate,
        string? LastDiagnosticsSentUtcDate = null,
        string? LastReportedAppVersion = null);

    internal sealed record TelemetryDiagnostics(
        string OsVersion,
        string DataSourceMode,
        bool WebServerEnabled,
        bool WebServerLanAccessEnabled,
        bool VatsimCallsignLookupEnabled,
        bool MapLayerEnabled,
        bool ControlledAirspaceLayerEnabled,
        bool LaraAirspaceEnabled,
        string OrientationMode,
        string DirectionReferenceMode,
        string LabelMode,
        string CategoryFilter,
        int RangeOptionCount,
        int KneepadPageCount)
    {
        public static TelemetryDiagnostics FromSettings(TacticalDisplaySettings settings) =>
            new(
                Environment.OSVersion.VersionString,
                settings.DataSourceMode,
                settings.EnableWebServer,
                settings.EnableWebServerLanAccess,
                settings.EnableVatsimCallsignLookup,
                settings.ShowMapLayer,
                settings.ShowControlledAirspaceLayer,
                settings.ShowAirspaceBoundaries,
                settings.OrientationMode.ToString(),
                settings.DirectionReferenceMode.ToString(),
                settings.LabelMode.ToString(),
                settings.CategoryFilter.ToString(),
                settings.RangeScaleOptionsNm.Length,
                settings.KneepadPages.Count);
    }

    private sealed record TelemetryRequest(
        string InstallationId,
        string AppVersion,
        string Event,
        TelemetryDiagnostics? Diagnostics);
}
