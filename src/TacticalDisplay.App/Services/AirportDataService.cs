using System.Globalization;
using System.IO;
using System.Net.Http;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class AirportDataService
{
    private static readonly Uri AirportsCsvUri = new("https://davidmegginson.github.io/ourairports-data/airports.csv");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string _cachePath;

    public AirportDataService(string configDirectory)
    {
        _cachePath = Path.Combine(configDirectory, "cache", "ourairports-airports.csv");
    }

    public async Task<IReadOnlyList<AirportMapPoint>> LoadAsync(CancellationToken cancellationToken)
    {
        var csv = await CsvCacheService.LoadCachedCsvAsync(_httpClient, AirportsCsvUri, _cachePath, cancellationToken);
        return ParseAirports(csv);
    }

    private static IReadOnlyList<AirportMapPoint> ParseAirports(string csv)
    {
        using var rows = CsvCacheService.ParseCsvRows(csv).GetEnumerator();
        if (!rows.MoveNext())
        {
            return [];
        }

        var header = rows.Current;
        var identIndex = CsvCacheService.IndexOf(header, "ident");
        var typeIndex = CsvCacheService.IndexOf(header, "type");
        var nameIndex = CsvCacheService.IndexOf(header, "name");
        var latitudeIndex = CsvCacheService.IndexOf(header, "latitude_deg");
        var longitudeIndex = CsvCacheService.IndexOf(header, "longitude_deg");

        if (identIndex < 0 || typeIndex < 0 || nameIndex < 0 || latitudeIndex < 0 || longitudeIndex < 0)
        {
            return [];
        }

        var airports = new List<AirportMapPoint>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.Count <= Math.Max(longitudeIndex, Math.Max(latitudeIndex, Math.Max(nameIndex, Math.Max(typeIndex, identIndex)))))
            {
                continue;
            }

            var type = row[typeIndex];
            if (type is not ("large_airport" or "medium_airport" or "small_airport" or "heliport"))
            {
                continue;
            }

            if (!double.TryParse(row[latitudeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(row[longitudeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                continue;
            }

            var ident = row[identIndex].Trim();
            if (string.IsNullOrWhiteSpace(ident))
            {
                continue;
            }

            airports.Add(new AirportMapPoint(
                ident,
                row[nameIndex].Trim(),
                type,
                latitude,
                longitude));
        }

        return airports;
    }
}
