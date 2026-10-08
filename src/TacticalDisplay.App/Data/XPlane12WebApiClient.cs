using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TacticalDisplay.App.Data;

internal sealed class XPlane12WebApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public XPlane12WebApiClient(string baseUrl, HttpClient? httpClient = null)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8086/" : baseUrl.Trim();
        BaseUri = value.EndsWith("/", StringComparison.Ordinal) ? new Uri(value) : new Uri($"{value}/");
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(header => header.MediaType == "application/json"))
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUri { get; }
    public string ApiVersion { get; private set; } = "v1";

    public async Task DiscoverApiVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(BaseUri, "api/capabilities"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            ApiVersion = "v1";
            return;
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("X-Plane Web API rejected the connection. Check Network security policy and incoming traffic settings.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var versions = document.RootElement.TryGetProperty("api", out var api) && api.TryGetProperty("versions", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];
        if (versions.Length == 0)
        {
            ApiVersion = "v1";
            return;
        }

        if (!versions.Contains("v1", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"X-Plane Web API capabilities do not advertise the supported v1 contract. Advertised versions: {string.Join(",", versions)}.");

        ApiVersion = "v1";
    }

    public async Task<long> ResolveDataRefIdAsync(string name, CancellationToken cancellationToken)
    {
        var relative = $"api/{ApiVersion}/datarefs?filter[name]={Uri.EscapeDataString(name)}";
        using var response = await _httpClient.GetAsync(new Uri(BaseUri, relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var data = document.RootElement.TryGetProperty("data", out var dataElement) ? dataElement : document.RootElement;
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException($"X-Plane dataref '{name}' was not found.");
        return data[0].GetProperty("id").GetInt64();
    }

    public async Task<JsonDocument> GetValueDocumentAsync(long dataRefId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(BaseUri, $"api/{ApiVersion}/datarefs/{dataRefId}/value"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task PatchValueAsync(long dataRefId, object data, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PatchAsJsonAsync(
            new Uri(BaseUri, $"api/{ApiVersion}/datarefs/{dataRefId}/value"), new { data }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

}
