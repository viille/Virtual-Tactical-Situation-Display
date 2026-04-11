using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class AirspaceDataService
{
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
        var activeAirspaces = await LoadActiveAirspaceNamesAsync(settings, cancellationToken);

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseGeoJson(document.RootElement, activeAirspaces);
    }

    private async Task<HashSet<string>> LoadActiveAirspaceNamesAsync(TacticalDisplaySettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AirspaceActivationUrl))
        {
            return [];
        }

        var text = await _httpClient.GetStringAsync(settings.AirspaceActivationUrl, cancellationToken);
        return ParseTopSkyActivationNames(text);
    }

    private static HashSet<string> ParseTopSkyActivationNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token.StartsWith("REFRESH_INTERVAL:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                parts[0].Equals("VLARA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(parts[0]);
        }

        return names;
    }

    private static IReadOnlyList<AirspaceArea> ParseGeoJson(JsonElement root, HashSet<string> activeAirspaces)
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
            airspaces.Add(new AirspaceArea(
                name,
                ReadString(properties, "type"),
                ReadNullableInt(properties, "lowerFL"),
                ReadNullableInt(properties, "upperFL"),
                activeAirspaces.Contains(name),
                polygons));
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
}
