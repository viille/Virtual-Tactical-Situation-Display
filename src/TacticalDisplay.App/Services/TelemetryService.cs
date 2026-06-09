using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.IO;

namespace TacticalDisplay.App.Services;

public sealed class TelemetryService
{
    private const string EndpointUrl = "https://vtsd-telemetry.vercel.app/api/telemetry";
    private const string IngestKeyHeaderName = "X-VTSD-Ingest-Key";
    private const string IngestKeyMetadataKey = "DefaultTelemetryIngestKey";
    private const string IngestKeyEnvironmentVariable = "VTSD_INGEST_KEY";
    private const string TelemetryEventName = "app_active";
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

    public void SendAppActivePingInBackground(string appVersion)
    {
        _ = Task.Run(() => SendAppActivePingAsync(appVersion, CancellationToken.None));
    }

    internal async Task SendAppActivePingAsync(string appVersion, CancellationToken cancellationToken)
    {
        var today = DateTimeOffset.UtcNow.ToString(UtcDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        var stateFileExists = File.Exists(_statePath);
        var state = LoadState();
        if (!stateFileExists)
        {
            SaveState(state);
        }

        var ingestKey = ResolveIngestKey();
        if (string.IsNullOrWhiteSpace(ingestKey))
        {
            DataSourceDebugLog.Debug("Telemetry", "Telemetry ingest key is not configured; skipping app_active ping");
            return;
        }

        if (string.Equals(state.LastSentUtcDate, today, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(state.InstallationId))
        {
            state = state with { InstallationId = Guid.NewGuid().ToString("D") };
            SaveState(state);
        }

        try
        {
            using var client = _httpClientFactory();
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
            request.Headers.Add(IngestKeyHeaderName, ingestKey.Trim());
            request.Content = JsonContent.Create(new TelemetryRequest(
                state.InstallationId,
                NormalizeAppVersion(appVersion),
                TelemetryEventName),
                options: JsonOptions);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DataSourceDebugLog.Debug("Telemetry", $"Telemetry app_active ping failed | status={(int)response.StatusCode}");
                return;
            }

            SaveState(state with { LastSentUtcDate = today });
            DataSourceDebugLog.Debug("Telemetry", "Telemetry app_active ping sent");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry app_active ping unavailable | {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Debug("Telemetry", $"Telemetry app_active ping skipped | {ex.GetType().Name}: {ex.Message}");
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

    private sealed record TelemetryState(string? InstallationId, string? LastSentUtcDate);

    private sealed record TelemetryRequest(string InstallationId, string AppVersion, string Event);
}
