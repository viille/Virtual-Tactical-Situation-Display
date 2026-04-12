using System.Globalization;
using System.IO;
using System.Net.Http;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class NavaidDataService
{
    private static readonly Uri NavaidsCsvUri = new("https://davidmegginson.github.io/ourairports-data/navaids.csv");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string _cachePath;

    public NavaidDataService(string configDirectory)
    {
        _cachePath = Path.Combine(configDirectory, "cache", "ourairports-navaids.csv");
    }

    public async Task<IReadOnlyList<NavaidMapPoint>> LoadAsync(CancellationToken cancellationToken)
    {
        var csv = await CsvCacheService.LoadCachedCsvAsync(_httpClient, NavaidsCsvUri, _cachePath, cancellationToken);
        return ParseNavaids(csv);
    }

    private static IReadOnlyList<NavaidMapPoint> ParseNavaids(string csv)
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
        var frequencyIndex = CsvCacheService.IndexOf(header, "frequency_khz");
        var latitudeIndex = CsvCacheService.IndexOf(header, "latitude_deg");
        var longitudeIndex = CsvCacheService.IndexOf(header, "longitude_deg");

        if (identIndex < 0 ||
            typeIndex < 0 ||
            nameIndex < 0 ||
            frequencyIndex < 0 ||
            latitudeIndex < 0 ||
            longitudeIndex < 0)
        {
            return [];
        }

        var maxRequiredIndex = new[] { identIndex, typeIndex, nameIndex, frequencyIndex, latitudeIndex, longitudeIndex }.Max();
        var navaids = new List<NavaidMapPoint>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.Count <= maxRequiredIndex)
            {
                continue;
            }

            var type = row[typeIndex].Trim().ToUpperInvariant();
            if (!IsSupportedNavaidType(type))
            {
                continue;
            }

            if (!double.TryParse(row[latitudeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(row[longitudeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                continue;
            }

            _ = double.TryParse(row[frequencyIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var frequencyKhz);
            var ident = row[identIndex].Trim();
            if (string.IsNullOrWhiteSpace(ident))
            {
                continue;
            }

            navaids.Add(new NavaidMapPoint(
                ident,
                row[nameIndex].Trim(),
                type,
                frequencyKhz,
                latitude,
                longitude));
        }

        return navaids;
    }

    private static bool IsSupportedNavaidType(string type) =>
        type is "VOR" or "VOR-DME" or "VORTAC" or "DME" or "NDB" or "NDB-DME" or "TACAN";
}
