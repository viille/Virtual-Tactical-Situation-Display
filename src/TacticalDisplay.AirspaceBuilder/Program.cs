using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;

const string CoverUrl = "https://www.ais.fi/eaip/currently_effective/EF-Cover%20Page-en-GB.html";
const string Enr21Url = "https://www.ais.fi/eaip/currently_effective/eAIP/EF-ENR%202.1-en-GB.html";
const string IndexUrl = "https://www.ais.fi/eaip/currently_effective/index.html";
const string OutputPath = "data/efin.geojson";
const string MetadataPath = "data/metadata.json";
const string SchemaVersion = "2";
var coordinateRegex = new Regex(@"(\d{6,7}[NS])\s*(\d{7,8}[EW])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var config = Configuration.Default;
var context = BrowsingContext.New(config);

var coverHtml = await http.GetStringAsync(CoverUrl);
var airacEffectiveDate = ParseEffectiveDate(coverHtml);

var existingMetadata = File.Exists(MetadataPath)
    ? JsonNode.Parse(await File.ReadAllTextAsync(MetadataPath))?.AsObject()
    : null;
var lastDate = existingMetadata?["airacEffectiveDate"]?.GetValue<string>();

if (string.Equals(lastDate, airacEffectiveDate, StringComparison.OrdinalIgnoreCase) && File.Exists(OutputPath))
{
    Console.WriteLine($"AIRAC unchanged ({airacEffectiveDate}); nothing to rebuild.");
    return;
}

var existingLaraFeatures = await ReadExistingLaraFeaturesAsync(OutputPath);

var enrDoc = await context.OpenAsync(req => req.Content(await http.GetStringAsync(Enr21Url)).Address(Enr21Url));
var ctas = ParseEnrTable(enrDoc, "CTA", "table 3", airacEffectiveDate);
var tmas = ParseEnrTable(enrDoc, "TMA", "table 4", airacEffectiveDate);

var ad2Urls = await DiscoverAd2AerodromeUrlsAsync(context, http);
var ctrs = new List<JsonObject>();
foreach (var ad2Url in ad2Urls)
{
    var html = await http.GetStringAsync(ad2Url);
    ctrs.AddRange(ParseCtrsFromAd217(html, ad2Url, airacEffectiveDate));
}

var allFeatures = new JsonArray();
foreach (var l in existingLaraFeatures)
{
    allFeatures.Add(l.DeepClone());
}

foreach (var c in ctas.Concat(tmas).Concat(ctrs))
{
    allFeatures.Add(c);
}

ValidateLaraPreservation(existingLaraFeatures, allFeatures);

var geo = new JsonObject
{
    ["type"] = "FeatureCollection",
    ["features"] = allFeatures
};

Directory.CreateDirectory("data");
var options = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(OutputPath, geo.ToJsonString(options));

var counts = CountByType(allFeatures);
var metadata = new JsonObject
{
    ["airacEffectiveDate"] = airacEffectiveDate,
    ["builtAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
    ["schemaVersion"] = SchemaVersion,
    ["sources"] = new JsonArray(CoverUrl, Enr21Url, IndexUrl),
    ["featureCounts"] = new JsonObject
    {
        ["CTR"] = counts.GetValueOrDefault("CTR"),
        ["TMA"] = counts.GetValueOrDefault("TMA"),
        ["CTA"] = counts.GetValueOrDefault("CTA"),
        ["LARA"] = counts.GetValueOrDefault("LARA")
    }
};
await File.WriteAllTextAsync(MetadataPath, metadata.ToJsonString(options));

Console.WriteLine($"Rebuilt {OutputPath} for AIRAC {airacEffectiveDate}");

static string ParseEffectiveDate(string html)
{
    var m = Regex.Match(html, @"Effective\s*date\s*[:\-]?\s*([0-9]{1,2}\s+[A-Z]{3}\s+[0-9]{4})", RegexOptions.IgnoreCase);
    if (!m.Success)
    {
        throw new InvalidOperationException("Could not parse AIRAC effective date from cover page.");
    }

    return m.Groups[1].Value.Trim().ToUpperInvariant();
}

static async Task<List<JsonObject>> ReadExistingLaraFeaturesAsync(string outputPath)
{
    if (!File.Exists(outputPath))
    {
        return [];
    }

    var root = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))?.AsObject();
    var features = root?["features"]?.AsArray() ?? [];
    var result = new List<JsonObject>();
    foreach (var f in features)
    {
        if (f is not JsonObject obj)
        {
            continue;
        }

        var props = obj["properties"] as JsonObject;
        var type = props?["type"]?.GetValue<string>();
        var sourceType = props?["sourceType"]?.GetValue<string>();
        if (string.Equals(sourceType, "LARA", StringComparison.OrdinalIgnoreCase) ||
            (type is not "CTR" and not "TMA" and not "CTA"))
        {
            result.Add(obj);
        }
    }

    return result;
}

static Dictionary<string, int> CountByType(JsonArray features)
{
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["CTR"] = 0,
        ["TMA"] = 0,
        ["CTA"] = 0,
        ["LARA"] = 0
    };

    foreach (var f in features)
    {
        if (f is not JsonObject obj)
        {
            continue;
        }

        var props = obj["properties"] as JsonObject;
        var type = props?["type"]?.GetValue<string>();
        var sourceType = props?["sourceType"]?.GetValue<string>();
        if (type is not null && counts.ContainsKey(type))
        {
            counts[type]++;
        }

        if (string.Equals(sourceType, "LARA", StringComparison.OrdinalIgnoreCase))
        {
            counts["LARA"]++;
        }
    }

    return counts;
}

static void ValidateLaraPreservation(List<JsonObject> originalLara, JsonArray mergedFeatures)
{
    var mergedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in mergedFeatures)
    {
        if (f is not JsonObject obj)
        {
            continue;
        }

        var props = obj["properties"] as JsonObject;
        if (string.Equals(props?["sourceType"]?.GetValue<string>(), "LARA", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "CTR", "TMA", "CTA" }.Contains(props?["type"]?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var id = props?["id"]?.GetValue<string>();
            var name = props?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) mergedNames.Add($"id:{id}");
            if (!string.IsNullOrWhiteSpace(name)) mergedNames.Add($"name:{name}");
        }
    }

    foreach (var lara in originalLara)
    {
        var props = lara["properties"] as JsonObject;
        var id = props?["id"]?.GetValue<string>();
        var name = props?["name"]?.GetValue<string>();
        var found = (!string.IsNullOrWhiteSpace(id) && mergedNames.Contains($"id:{id}")) ||
                    (!string.IsNullOrWhiteSpace(name) && mergedNames.Contains($"name:{name}"));
        if (!found)
        {
            throw new InvalidOperationException($"LARA feature missing after rebuild: {name ?? id ?? "<unknown>"}");
        }
    }
}

static List<JsonObject> ParseEnrTable(IDocument doc, string airspaceType, string sourceSection, string airacEffectiveDate)
{
    var table = FindTableByLabel(doc, sourceSection)
        ?? throw new InvalidOperationException($"Could not find {sourceSection} in ENR 2.1.");

    return ParseRowsFromTable(table, airspaceType, "ControlledAirspace", sourceSection, airacEffectiveDate);
}

static IElement? FindTableByLabel(IDocument doc, string label)
{
    var marker = doc.All.FirstOrDefault(e => e.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));
    if (marker is null)
    {
        return null;
    }

    if (marker.TagName.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
    {
        return marker;
    }

    return marker.NextElementSibling?.TagName.Equals("TABLE", StringComparison.OrdinalIgnoreCase) == true
        ? marker.NextElementSibling
        : marker.ParentElement?.QuerySelector("table");
}

static List<JsonObject> ParseCtrsFromAd217(string html, string sourceUrl, string airacEffectiveDate)
{
    var ad217Start = html.IndexOf("AD 2.17", StringComparison.OrdinalIgnoreCase);
    if (ad217Start < 0)
    {
        return [];
    }

    var ad218Start = html.IndexOf("AD 2.18", ad217Start, StringComparison.OrdinalIgnoreCase);
    var chunk = ad218Start > ad217Start ? html[ad217Start..ad218Start] : html[ad217Start..];

    var context = BrowsingContext.New(Configuration.Default);
    var doc = context.OpenAsync(req => req.Content(chunk)).GetAwaiter().GetResult();

    var table = doc.QuerySelector("table") ?? throw new InvalidOperationException($"AD 2.17 table missing in {sourceUrl}");
    if (ContainsUnsupportedGeometry(table.TextContent))
    {
        throw new InvalidOperationException($"Unsupported arc/radius geometry in {sourceUrl}");
    }

    return ParseRowsFromTable(table, "CTR", "ControlledAirspace", "AD 2.17", airacEffectiveDate, sourceUrl);
}

static bool ContainsUnsupportedGeometry(string text) =>
    text.Contains("arc", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("radius", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("clockwise", StringComparison.OrdinalIgnoreCase) ||
    text.Contains("anticlockwise", StringComparison.OrdinalIgnoreCase);

static List<JsonObject> ParseRowsFromTable(IElement table, string defaultType, string sourceType, string sourceSection, string airacEffectiveDate, string? sourceOverride = null)
{
    var rows = table.QuerySelectorAll("tr");
    var output = new List<JsonObject>();
    foreach (var row in rows.Skip(1))
    {
        var cells = row.QuerySelectorAll("td").Select(c => Normalize(c.TextContent)).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (cells.Count < 2)
        {
            continue;
        }

        var name = cells[0];
        var lateralText = cells.FirstOrDefault(c => c.Contains("Area bounded by", StringComparison.OrdinalIgnoreCase) || coordinateRegex.IsMatch(c))
                          ?? cells.ElementAtOrDefault(1)
                          ?? string.Empty;

        var coords = ParseBoundaryCoordinates(lateralText, coordinateRegex);
        if (coords.Count < 3)
        {
            continue;
        }

        var verticalText = cells.FirstOrDefault(c => c.Contains("FL", StringComparison.OrdinalIgnoreCase) || c.Contains("FT", StringComparison.OrdinalIgnoreCase))
                           ?? string.Empty;
        var limits = ParseVerticalLimits(verticalText);

        var geometry = new JsonObject
        {
            ["type"] = "Polygon",
            ["coordinates"] = BuildPolygonCoordinates(coords)
        };

        var properties = new JsonObject
        {
            ["id"] = BuildId(defaultType, name),
            ["name"] = name,
            ["type"] = defaultType,
            ["class"] = defaultType,
            ["source"] = sourceOverride ?? Enr21Url,
            ["sourceSection"] = sourceSection,
            ["airacEffectiveDate"] = airacEffectiveDate,
            ["sourceType"] = sourceType
        };

        if (limits.lowerFl.HasValue)
        {
            properties["lowerFL"] = limits.lowerFl.Value;
        }

        if (limits.upperFl.HasValue)
        {
            properties["upperFL"] = limits.upperFl.Value;
        }

        if (!string.IsNullOrWhiteSpace(limits.lowerRaw))
        {
            properties["lowerLimitRaw"] = limits.lowerRaw;
        }

        if (!string.IsNullOrWhiteSpace(limits.upperRaw))
        {
            properties["upperLimitRaw"] = limits.upperRaw;
        }

        var feature = new JsonObject
        {
            ["type"] = "Feature",
            ["properties"] = properties,
            ["geometry"] = geometry
        };

        output.Add(feature);
    }

    return output;
}

static string Normalize(string value) => Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();

static string BuildId(string type, string name)
{
    var slug = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
    return $"{type}-{slug}";
}

static JsonArray BuildPolygonCoordinates(List<(double lat, double lon)> coords)
{
    if (coords[0] != coords[^1])
    {
        coords.Add(coords[0]);
    }

    var ring = new JsonArray();
    foreach (var (lat, lon) in coords)
    {
        ring.Add(new JsonArray(lon, lat));
    }

    return new JsonArray(ring);
}

static List<(double lat, double lon)> ParseBoundaryCoordinates(string text, Regex coordinateRegex)
{
    var coords = new List<(double lat, double lon)>();
    foreach (Match m in coordinateRegex.Matches(text))
    {
        var lat = ParseDmsToDecimal(m.Groups[1].Value);
        var lon = ParseDmsToDecimal(m.Groups[2].Value);
        coords.Add((lat, lon));
    }

    return coords;
}

static double ParseDmsToDecimal(string token)
{
    token = token.Trim().ToUpperInvariant();
    var hemi = token[^1];
    var digits = token[..^1];

    int degLen = digits.Length - 4;
    var deg = int.Parse(digits[..degLen], CultureInfo.InvariantCulture);
    var min = int.Parse(digits.Substring(degLen, 2), CultureInfo.InvariantCulture);
    var sec = int.Parse(digits.Substring(degLen + 2, 2), CultureInfo.InvariantCulture);

    var value = deg + (min / 60d) + (sec / 3600d);
    if (hemi is 'S' or 'W')
    {
        value *= -1;
    }

    return value;
}

static (int? lowerFl, int? upperFl, string? lowerRaw, string? upperRaw) ParseVerticalLimits(string text)
{
    var clean = Normalize(text);
    if (string.IsNullOrWhiteSpace(clean))
    {
        return (null, null, null, null);
    }

    var flMatches = Regex.Matches(clean, @"FL\s*(\d{1,3})", RegexOptions.IgnoreCase)
        .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
        .ToList();

    if (Regex.IsMatch(clean, @"^FL\s*\d{1,3}\s*/\s*FL\s*\d{1,3}$", RegexOptions.IgnoreCase) && flMatches.Count == 2)
    {
        return (flMatches[1], flMatches[0], clean.Split('/')[1].Trim(), clean.Split('/')[0].Trim());
    }

    var parts = clean.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    string? lowerRaw = parts.Length >= 2 ? parts[1] : (parts.Length == 1 ? parts[0] : null);
    string? upperRaw = parts.Length >= 1 ? parts[0] : null;

    int? lower = lowerRaw is not null && Regex.IsMatch(lowerRaw, @"^FL\s*\d{1,3}$", RegexOptions.IgnoreCase)
        ? int.Parse(Regex.Match(lowerRaw, @"\d+").Value, CultureInfo.InvariantCulture)
        : null;
    int? upper = upperRaw is not null && Regex.IsMatch(upperRaw, @"^FL\s*\d{1,3}$", RegexOptions.IgnoreCase)
        ? int.Parse(Regex.Match(upperRaw, @"\d+").Value, CultureInfo.InvariantCulture)
        : null;

    return (lower, upper, lowerRaw, upperRaw);
}

static async Task<List<string>> DiscoverAd2AerodromeUrlsAsync(IBrowsingContext context, HttpClient http)
{
    var indexDoc = await context.OpenAsync(req => req.Content(await http.GetStringAsync(IndexUrl)).Address(IndexUrl));
    var links = indexDoc.QuerySelectorAll("a[href]")
        .Select(a => a.GetAttribute("href") ?? string.Empty)
        .Where(h => Regex.IsMatch(h, @"AD-2\.[A-Z]{4}.*-en-GB\.html", RegexOptions.IgnoreCase))
        .Select(h => new Uri(new Uri(IndexUrl), h).ToString())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    return links;
}
