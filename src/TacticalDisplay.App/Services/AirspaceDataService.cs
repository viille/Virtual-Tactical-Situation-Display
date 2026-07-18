using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class AirspaceDataService : IDisposable
{
    private static readonly TimeSpan StaticAirspaceCacheMaxAge = TimeSpan.FromHours(24);
    private const string LocalAdizResourceName = "TacticalDisplay.App.Data.ADIZ.geojson";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<IReadOnlyList<AirspaceArea>> LoadAsync(TacticalDisplaySettings settings, CancellationToken cancellationToken)
    {
        var baseUrl = settings.AirspaceDataBaseUrl.TrimEnd('/');
        var activeAirspaces = await LoadActiveAirspacesBestEffortAsync(settings, cancellationToken);
        var airspaces = new List<AirspaceArea>();

        foreach (var firCode in ResolveAirspaceFirCodes(settings))
        {
            var url = $"{baseUrl}/{firCode}.geojson";
            try
            {
                using var document = await LoadStaticAirspaceDocumentAsync(url, firCode, cancellationToken);
                airspaces.AddRange(ParseGeoJson(document.RootElement, activeAirspaces));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Warn("Airspace", $"Static airspace unavailable | fir={firCode} error={ex.Message}");
            }
        }

        airspaces = MergeAirspacesByName(airspaces);
        return await AddLocalAdizAirspacesAsync(airspaces, cancellationToken);
    }

    private async Task<Dictionary<string, AirspaceActivation>> LoadActiveAirspacesBestEffortAsync(
        TacticalDisplaySettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LoadActiveAirspacesAsync(settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("Airspace", $"Active reservation feed unavailable; loading static airspace only | error={ex.Message}");
            return [];
        }
    }

    private async Task<JsonDocument> LoadStaticAirspaceDocumentAsync(
        string url,
        string firCode,
        CancellationToken cancellationToken)
    {
        var cachePath = ResolveStaticAirspaceCachePath(url, firCode);
        if (IsFreshCache(cachePath, StaticAirspaceCacheMaxAge))
        {
            return await ReadJsonDocumentFromFileAsync(cachePath, cancellationToken);
        }

        var tempPath = $"{cachePath}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (!File.Exists(cachePath))
            {
                throw;
            }
        }

        return await ReadJsonDocumentFromFileAsync(cachePath, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonDocumentFromFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool IsFreshCache(string path, TimeSpan maxAge) =>
        File.Exists(path) &&
        DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge;

    private static string ResolveStaticAirspaceCachePath(string url, string firCode)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        return Path.Combine(AppDataPaths.AirspaceCacheDirectory, $"{firCode}-{hash}.geojson");
    }

    private async Task<Dictionary<string, AirspaceActivation>> LoadActiveAirspacesAsync(TacticalDisplaySettings settings, CancellationToken cancellationToken)
    {
        var urls = ResolveAirspaceActivationUrls(settings);
        if (urls.Count == 0)
        {
            return [];
        }

        var activations = new Dictionary<string, AirspaceActivation>(StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var url in urls)
        {
            try
            {
                var text = await _httpClient.GetStringAsync(url, cancellationToken);
                using var document = JsonDocument.Parse(text);
                foreach (var (name, activation) in ParseReservationActivations(document.RootElement, nowUtc))
                {
                    activations[name] = activation;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Warn("Airspace", $"Active reservation feed unavailable | url={url} error={ex.Message}");
            }
        }

        return activations;
    }

    private static Dictionary<string, AirspaceActivation> ParseReservationActivations(JsonElement root, DateTimeOffset nowUtc)
    {
        var activations = new Dictionary<string, AirspaceActivation>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Array)
        {
            return activations;
        }

        foreach (var reservation in root.EnumerateArray())
        {
            var hasActiveStatus = string.Equals(
                ReadString(reservation, "status"),
                "active",
                StringComparison.OrdinalIgnoreCase);
            var isWithinActivationWindow =
                TryReadUnixMilliseconds(reservation, "start", out var activeFromUtc) &&
                TryReadUnixMilliseconds(reservation, "end", out var activeUntilUtc) &&
                nowUtc >= activeFromUtc &&
                nowUtc <= activeUntilUtc;

            if ((!hasActiveStatus && !isWithinActivationWindow) ||
                !reservation.TryGetProperty("areas", out var areas) ||
                areas.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var area in areas.EnumerateArray())
            {
                var name = ReadString(area, "area_id");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var polygons = area.TryGetProperty("coordinates", out var coordinates)
                    ? ParsePolygonCoordinates(coordinates)
                    : [];
                activations[name] = new AirspaceActivation(
                    FlightLevelToAltitudeFt(ReadNullableInt(area, "lower")),
                    FlightLevelToAltitudeFt(ReadNullableInt(area, "upper")),
                    polygons);
            }
        }

        return activations;
    }

    private static IReadOnlyList<AirspaceArea> ParseGeoJson(JsonElement root, IReadOnlyDictionary<string, AirspaceActivation> activeAirspaces)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var airspaces = new List<AirspaceArea>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) ||
                !geometry.TryGetProperty("type", out var geometryType) ||
                !geometry.TryGetProperty("coordinates", out var coordinates))
            {
                continue;
            }

            var polygons = geometryType.GetString() switch
            {
                "Polygon" => ParsePolygonCoordinates(coordinates),
                "MultiPolygon" => ParseMultiPolygonCoordinates(coordinates),
                _ => []
            };

            if (polygons.Count == 0)
            {
                continue;
            }

            var properties = feature.TryGetProperty("properties", out var p) ? p : default;
            var name = ReadString(properties, "name");
            var isActive = activeAirspaces.TryGetValue(name, out var activation);
            airspaces.Add(new AirspaceArea(
                name,
                ReadString(properties, "type"),
                ReadNullableInt(properties, "lowerFL"),
                ReadNullableInt(properties, "upperFL"),
                isActive,
                activation?.LowerAltitudeFt,
                activation?.UpperAltitudeFt,
                polygons));
        }

        foreach (var (name, activation) in activeAirspaces)
        {
            if (airspaces.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                activation.Polygons.Count == 0)
            {
                continue;
            }

            airspaces.Add(new AirspaceArea(
                name,
                "Reservation",
                null,
                null,
                true,
                activation.LowerAltitudeFt,
                activation.UpperAltitudeFt,
                activation.Polygons));
        }

        return airspaces;
    }

    private static async Task<IReadOnlyList<AirspaceArea>> AddLocalAdizAirspacesAsync(
        IReadOnlyList<AirspaceArea> airspaces,
        CancellationToken cancellationToken)
    {
        await using var stream = typeof(AirspaceDataService).Assembly.GetManifestResourceStream(LocalAdizResourceName);
        if (stream is null)
        {
            return airspaces;
        }

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var localAirspaces = ParseGeoJson(document.RootElement, new Dictionary<string, AirspaceActivation>());
        if (localAirspaces.Count == 0)
        {
            return airspaces;
        }

        var names = new HashSet<string>(airspaces.Select(static area => area.Name), StringComparer.OrdinalIgnoreCase);
        var merged = new List<AirspaceArea>(airspaces);
        foreach (var localAirspace in localAirspaces)
        {
            if (string.IsNullOrWhiteSpace(localAirspace.Name) || !names.Add(localAirspace.Name))
            {
                continue;
            }

            merged.Add(localAirspace);
        }

        return merged;
    }

    private static IReadOnlyList<string> ResolveAirspaceFirCodes(TacticalDisplaySettings settings)
    {
        var firCodes = settings.AirspaceFirCodes.Length > 0
            ? settings.AirspaceFirCodes
            : [settings.AirspaceFirCode];

        return firCodes
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveAirspaceActivationUrls(TacticalDisplaySettings settings)
    {
        var firCodes = ResolveAirspaceFirCodes(settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var urls = new List<string>();
        if (firCodes.Contains("efin"))
        {
            urls.Add("https://lara-backend.lusep.fi/data/reservations/efin.json");
        }

        if (firCodes.Contains("eett"))
        {
            urls.Add("https://lara-backend.lusep.fi/data/reservations/eett.json");
        }

        if (urls.Count == 0)
        {
            urls.AddRange(settings.AirspaceActivationUrls.Length > 0
                ? settings.AirspaceActivationUrls
                : [settings.AirspaceActivationUrl]);
        }

        return urls
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<AirspaceArea> MergeAirspacesByName(IEnumerable<AirspaceArea> airspaces)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<AirspaceArea>();
        foreach (var airspace in airspaces)
        {
            if (string.IsNullOrWhiteSpace(airspace.Name) || !names.Add(airspace.Name))
            {
                continue;
            }

            merged.Add(airspace);
        }

        return merged;
    }

    private static IReadOnlyList<AirspacePolygon> ParsePolygonCoordinates(JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var exterior = coordinates.EnumerateArray().FirstOrDefault();
        var polygon = ParseLinearRing(exterior);
        return polygon is null ? [] : [polygon];
    }

    private static IReadOnlyList<AirspacePolygon> ParseMultiPolygonCoordinates(JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var polygons = new List<AirspacePolygon>();
        foreach (var polygonCoordinates in coordinates.EnumerateArray())
        {
            var exterior = polygonCoordinates.EnumerateArray().FirstOrDefault();
            var polygon = ParseLinearRing(exterior);
            if (polygon is not null)
            {
                polygons.Add(polygon);
            }
        }

        return polygons;
    }

    private static AirspacePolygon? ParseLinearRing(JsonElement ring)
    {
        if (ring.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var points = new List<AirspaceCoordinate>();
        foreach (var pair in ring.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
            {
                continue;
            }

            var longitude = pair[0].GetDouble();
            var latitude = pair[1].GetDouble();
            points.Add(new AirspaceCoordinate(latitude, longitude));
        }

        return points.Count < 2 ? null : new AirspacePolygon(points);
    }

    private static string ReadString(JsonElement properties, string name)
    {
        if (properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static int? ReadNullableInt(JsonElement properties, string name)
    {
        if (properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static bool TryReadUnixMilliseconds(JsonElement properties, string name, out DateTimeOffset value)
    {
        value = default;
        if (properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty(name, out var property))
        {
            return false;
        }

        long milliseconds;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            milliseconds = number;
        }
        else if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            milliseconds = number;
        }
        else
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static int? FlightLevelToAltitudeFt(int? flightLevel) => flightLevel * 100;

    public void Dispose() => _httpClient.Dispose();

    private sealed record AirspaceActivation(
        int? LowerAltitudeFt,
        int? UpperAltitudeFt,
        IReadOnlyList<AirspacePolygon> Polygons);
}
