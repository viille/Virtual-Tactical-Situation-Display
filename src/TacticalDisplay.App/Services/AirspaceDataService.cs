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
        var activeAirspaces = await LoadActiveAirspacesAsync(settings, cancellationToken);

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseGeoJson(document.RootElement, activeAirspaces);
    }

    private async Task<Dictionary<string, AirspaceActivation>> LoadActiveAirspacesAsync(TacticalDisplaySettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AirspaceActivationUrl))
        {
            return [];
        }

        var text = await _httpClient.GetStringAsync(settings.AirspaceActivationUrl, cancellationToken);
        return ParseTopSkyActivations(text, DateTimeOffset.UtcNow);
    }

    private static Dictionary<string, AirspaceActivation> ParseTopSkyActivations(string text, DateTimeOffset nowUtc)
    {
        var activations = new Dictionary<string, AirspaceActivation>(StringComparer.OrdinalIgnoreCase);
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

            var name = parts[0];
            if (!TryParseActivationWindow(parts, out var activeFromUtc, out var activeUntilUtc) ||
                nowUtc < activeFromUtc ||
                nowUtc > activeUntilUtc)
            {
                continue;
            }

            activations[name] = new AirspaceActivation(
                ReadNullableIntPart(parts, 6),
                ReadNullableIntPart(parts, 7));
        }

        return activations;
    }

    private static bool TryParseActivationWindow(string[] parts, out DateTimeOffset activeFromUtc, out DateTimeOffset activeUntilUtc)
    {
        activeFromUtc = default;
        activeUntilUtc = default;
        if (parts.Length <= 5)
        {
            return false;
        }

        if (!TryParseTopSkyDateTime(parts[1], parts[4], out activeFromUtc) ||
            !TryParseTopSkyDateTime(parts[2], parts[5], out activeUntilUtc))
        {
            return false;
        }

        if (activeUntilUtc < activeFromUtc)
        {
            activeUntilUtc = activeUntilUtc.AddDays(1);
        }

        return true;
    }

    private static bool TryParseTopSkyDateTime(string dateText, string timeText, out DateTimeOffset value)
    {
        value = default;
        if (dateText.Length != 6 ||
            timeText.Length != 4 ||
            !int.TryParse(dateText[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(dateText.Substring(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(dateText.Substring(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(timeText[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(timeText.Substring(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        try
        {
            if (hour == 24 && minute == 0)
            {
                value = new DateTimeOffset(2000 + year, month, day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
                return true;
            }

            if (hour is < 0 or > 23 || minute is < 0 or > 59)
            {
                return false;
            }

            value = new DateTimeOffset(2000 + year, month, day, hour, minute, 0, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
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

    private static int? ReadNullableIntPart(string[] parts, int index)
    {
        if (index >= parts.Length ||
            !int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return value;
    }

    private sealed record AirspaceActivation(int? LowerAltitudeFt, int? UpperAltitudeFt);
}
