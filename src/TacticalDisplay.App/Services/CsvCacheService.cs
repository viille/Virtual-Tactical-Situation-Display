using System.IO;
using System.Net.Http;
using System.Text;

namespace TacticalDisplay.App.Services;

internal static class CsvCacheService
{
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    public static async Task<string> LoadCachedCsvAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath) ||
            DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) >= CacheMaxAge)
        {
            await RefreshCacheAsync(httpClient, sourceUri, cachePath, cancellationToken);
        }

        return await File.ReadAllTextAsync(cachePath, Encoding.UTF8, cancellationToken);
    }

    public static IEnumerable<IReadOnlyList<string>> ParseCsvRows(string csv)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' && !inQuotes)
            {
                row.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().TrimEnd('\r'));
            yield return row;
        }
    }

    public static int IndexOf(IReadOnlyList<string> values, string name)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task RefreshCacheAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string cachePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempPath = $"{cachePath}.tmp";
        try
        {
            await using var stream = await httpClient.GetStreamAsync(sourceUri, cancellationToken);
            await using var file = File.Create(tempPath);
            await stream.CopyToAsync(file, cancellationToken);
            file.Close();
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
    }
}
