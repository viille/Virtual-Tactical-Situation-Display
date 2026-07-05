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
            collection.ShowKneepadPages = entry.ShowKneepadPages; collection.ShowMapFeaturesOnRadar = entry.ShowMapFeaturesOnRadar; collection.CacheOffline = entry.CacheOffline; collection.IsActive = entry.IsActive;
        }
    }
    public void Save(IEnumerable<Collection> collections)
    {
        var entries = collections.ToDictionary(x => x.Slug, x => new Entry
        {
            ShowKneepadPages = x.ShowKneepadPages,
            ShowMapFeaturesOnRadar = x.ShowMapFeaturesOnRadar,
            CacheOffline = x.CacheOffline,
            IsActive = x.IsActive
        }, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions.Options)); File.Move(temp, _path, true);
    }
    private sealed class Entry
    {
        public bool ShowKneepadPages { get; set; } = true;
        public bool ShowMapFeaturesOnRadar { get; set; }
        public bool CacheOffline { get; set; } = true;
        public bool IsActive { get; set; }
    }
}
