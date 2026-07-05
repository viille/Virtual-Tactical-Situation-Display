using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TacticalDisplay.App.Security;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App.Cloud;

public sealed class VtsdCloudClient
{
    private readonly HttpClient _http;
    private readonly ISecureTokenStore _tokens;

    public VtsdCloudClient(HttpClient http, CloudOptions options, ISecureTokenStore tokens)
    {
        _http = http; _http.BaseAddress = options.BaseUri; _http.Timeout = TimeSpan.FromSeconds(20); _tokens = tokens;
    }

    public Task<DeviceLoginStartResponse> StartDeviceLoginAsync(CancellationToken ct) =>
        SendAsync<DeviceLoginStartResponse>(HttpMethod.Post, "api/v1/auth/device/start", null, false, ct);
    public Task<DeviceLoginStatusResponse> GetDeviceLoginStatusAsync(string requestId, string requestToken, CancellationToken ct) =>
        SendAsync<DeviceLoginStatusResponse>(HttpMethod.Post, "api/v1/auth/device/status", new { requestId, requestToken }, false, ct);
    public Task<MeResponse> GetMeAsync(CancellationToken ct) => SendAsync<MeResponse>(HttpMethod.Get, "api/v1/me", null, true, ct);
    public Task LogoutAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "api/v1/auth/logout", null, true, ct);
    public Task<CollectionsResponse> ListCollectionsAsync(CancellationToken ct) => SendAsync<CollectionsResponse>(HttpMethod.Get, "api/v1/collections", null, false, ct, addTokenWhenAvailable: true);
    public Task<RedeemShareCodeResponse> RedeemShareCodeAsync(string code, CancellationToken ct) =>
        SendAsync<RedeemShareCodeResponse>(HttpMethod.Post, "api/v1/share-codes/redeem", new { code }, true, ct);
    public Task<CollectionPagesResponse> GetCollectionPagesAsync(string slug, CancellationToken ct) =>
        SendAsync<CollectionPagesResponse>(HttpMethod.Get, $"api/v1/collections/{Uri.EscapeDataString(slug)}/pages", null, false, ct, true);
    public Task<CollectionMapFeaturesResponse> GetCollectionMapFeaturesAsync(string slug, CancellationToken ct) =>
        SendAsync<CollectionMapFeaturesResponse>(HttpMethod.Get, $"api/v1/collections/{Uri.EscapeDataString(slug)}/map-features", null, false, ct, true);
    public Task SendTelemetryEventAsync(TelemetryEventRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/v1/telemetry", request, false, ct, true);

    private async Task SendAsync(HttpMethod method, string path, object? body, bool requiresToken, CancellationToken ct, bool addTokenWhenAvailable = false)
    {
        using var response = await SendCoreAsync(method, path, body, requiresToken, addTokenWhenAvailable, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool requiresToken, CancellationToken ct, bool addTokenWhenAvailable = false)
    {
        using var response = await SendCoreAsync(method, path, body, requiresToken, addTokenWhenAvailable, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, JsonOptions.Options)
                   ?? throw new JsonException("Empty response.");
        }
        catch (JsonException ex)
        {
            var preview = await ReadPreviewAsync(response, ct).ConfigureAwait(false);
            DataSourceDebugLog.Warn("Cloud", $"Cloud response could not be parsed | path={path} status={(int)response.StatusCode} contentType={response.Content.Headers.ContentType?.MediaType ?? "unknown"} error={ex.Message} body={preview}");
            throw new CloudApiException("VTSD Cloud returned a response this app version does not understand.", response.StatusCode, "MALFORMED_RESPONSE", inner: ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, bool requiresToken, bool addTokenWhenAvailable, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            var token = requiresToken || addTokenWhenAvailable ? await _tokens.GetSessionTokenAsync().ConfigureAwait(false) : null;
            if (requiresToken && string.IsNullOrWhiteSpace(token)) throw new CloudApiException("Sign in with VATSIM first.", System.Net.HttpStatusCode.Unauthorized, "UNAUTHORIZED");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions.Options);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            DataSourceDebugLog.Warn("Cloud", $"Cloud request timed out | method={method} path={path}");
            throw new CloudApiException("VTSD Cloud request timed out.", errorCode: "TIMEOUT", isNetworkError: true);
        }
        catch (HttpRequestException ex)
        {
            DataSourceDebugLog.Warn("Cloud", $"Cloud request unavailable | method={method} path={path} error={ex.Message}");
            throw new CloudApiException("VTSD Cloud is offline or unreachable.", errorCode: "NETWORK_ERROR", isNetworkError: true, inner: ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null; string? message = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var nested))
            {
                if (nested.ValueKind == JsonValueKind.Object) root = nested;
                else if (nested.ValueKind == JsonValueKind.String) code = nested.GetString();
            }
            if (root.TryGetProperty("code", out var codeNode)) code = codeNode.GetString();
            if (root.TryGetProperty("message", out var messageNode)) message = messageNode.GetString();
        }
        catch (JsonException) { }
        message ??= response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "The request was invalid.",
            System.Net.HttpStatusCode.Unauthorized => "Sign in with VATSIM first.",
            System.Net.HttpStatusCode.Forbidden => "You do not have permission for this action.",
            System.Net.HttpStatusCode.NotFound => "The requested Cloud content was not found.",
            System.Net.HttpStatusCode.TooManyRequests => "Too many attempts. Try again later.",
            _ => "VTSD Cloud could not complete the request."
        };
        DataSourceDebugLog.Warn("Cloud", $"Cloud request failed | status={(int)response.StatusCode} code={code ?? "none"} message={message}");
        throw new CloudApiException(message, response.StatusCode, code);
    }

    private static async Task<string> ReadPreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 240) body = body[..240];
            return body.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        }
        catch
        {
            return "<unavailable>";
        }
    }
}
