using System.IO;
using System.Text.Json;
using TacticalDisplay.App.Cloud;

namespace TacticalDisplay.App.Storage;

public sealed class CloudOverlaySettingsStore
{
    private readonly string _path;
    public CloudOverlaySettingsStore(string path) => _path = path;
    public void Apply(IEnumerable<Collection> collections)
    {
        Dictionary<string, Entry> entries;
        try { entries = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), JsonOptions.Options) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { entries = []; }
        foreach (var collection in collections)
        {
            if (!entries.TryGetValue(collection.Slug, out var entry)) continue;
            collection.ShowKneepadPages = entry.ShowKneepadPages; collection.ShowMapFeaturesOnRadar = entry.ShowMapFeaturesOnRadar; collection.CacheOffline = entry.CacheOffline;
        }
    }
    public void Save(IEnumerable<Collection> collections)
    {
        var entries = collections.ToDictionary(x => x.Slug, x => new Entry(x.ShowKneepadPages, x.ShowMapFeaturesOnRadar, x.CacheOffline), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions.Options)); File.Move(temp, _path, true);
    }
    private sealed record Entry(bool ShowKneepadPages, bool ShowMapFeaturesOnRadar, bool CacheOffline);
}
