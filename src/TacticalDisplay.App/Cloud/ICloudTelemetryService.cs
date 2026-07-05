namespace TacticalDisplay.App.Cloud;

public interface ICloudTelemetryService
{
    Task TrackAsync(string eventName, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default);
}

public sealed class CloudTelemetryService : ICloudTelemetryService
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "app_started", "app_closed", "cloud_login_started", "cloud_login_completed", "cloud_login_failed",
        "collections_fetched", "collection_synced", "share_code_redeemed", "kneepad_page_opened",
        "map_feature_overlay_enabled", "sync_failed", "api_error"
    };
    private static readonly string[] ForbiddenKeyParts = ["token", "code", "markdown", "content", "note", "brief", "coordinate", "latitude", "longitude"];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedPayloadKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["collections_fetched"] = new(StringComparer.Ordinal) { "count" },
        ["api_error"] = new(StringComparer.Ordinal) { "status", "errorType" }
    };
    private readonly VtsdCloudClient _client;
    public CloudTelemetryService(VtsdCloudClient client) => _client = client;

    public async Task TrackAsync(string eventName, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
    {
        if (!AllowedEvents.Contains(eventName)) throw new ArgumentException("Unsupported telemetry event.", nameof(eventName));
        var allowedKeys = AllowedPayloadKeys.TryGetValue(eventName, out var configured) ? configured : [];
        if (payload?.Keys.Any(key => !allowedKeys.Contains(key)) == true)
            throw new ArgumentException("Telemetry payload field is not allowed for this event.", nameof(payload));
        if (payload?.Keys.Any(key => ForbiddenKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase))) == true)
            throw new ArgumentException("Telemetry payload contains a sensitive field.", nameof(payload));
        if (payload?.Values.Any(value => value is not null and not string and not bool and not int and not long and not double and not decimal) == true)
            throw new ArgumentException("Telemetry payload values must be primitive.", nameof(payload));
        if (payload?.Values.OfType<string>().Any(value => value.Length > 64 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))) == true)
            throw new ArgumentException("Telemetry string value is not a safe identifier.", nameof(payload));
        try { await _client.SendTelemetryEventAsync(new TelemetryEventRequest { EventName = eventName, Payload = payload }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is CloudApiException or OperationCanceledException) { }
    }
}

internal static class CloudTelemetryExtensions
{
    public static Task TrackApiErrorAsync(this ICloudTelemetryService telemetry, CloudApiException error) =>
        telemetry.TrackAsync("api_error", new Dictionary<string, object?>
        {
            ["status"] = error.StatusCode is null ? 0 : (int)error.StatusCode,
            ["errorType"] = Normalize(error.ErrorCode)
        });
    private static string Normalize(string? value)
    {
        var normalized = new string((value ?? "UNKNOWN").Take(64).Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "UNKNOWN" : normalized;
    }
}
