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

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<IReadOnlyList<AirspaceArea>> LoadAsync(TacticalDisplaySettings settings, CancellationToken cancellationToken)
    {
        var firCode = string.IsNullOrWhiteSpace(settings.AirspaceFirCode)
            ? "efin"
            : settings.AirspaceFirCode.Trim().ToLowerInvariant();
        var baseUrl = settings.AirspaceDataBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{firCode}.geojson";
        var activeAirspaces = await LoadActiveAirspacesAsync(settings, cancellationToken);

        using var document = await LoadStaticAirspaceDocumentAsync(url, firCode, cancellationToken);
        return ParseGeoJson(document.RootElement, activeAirspaces);
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
        if (string.IsNullOrWhiteSpace(settings.AirspaceActivationUrl))
        {
            return [];
        }

        var text = await _httpClient.GetStringAsync(settings.AirspaceActivationUrl, cancellationToken);
        using var document = JsonDocument.Parse(text);
        return ParseReservationActivations(document.RootElement, DateTimeOffset.UtcNow);
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
            if (!TryReadUnixMilliseconds(reservation, "start", out var activeFromUtc) ||
                !TryReadUnixMilliseconds(reservation, "end", out var activeUntilUtc) ||
                nowUtc < activeFromUtc ||
                nowUtc > activeUntilUtc ||
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
