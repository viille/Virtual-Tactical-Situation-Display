using System.IO;
using System.Text.Json;
using TacticalDisplay.App.Cloud;

namespace TacticalDisplay.App.Storage;

public sealed class CloudPreferencesStore
{
    private readonly string _path;
    public CloudPreferencesStore(string path) => _path = path;
    public CloudPreferences Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<CloudPreferences>(File.ReadAllText(_path), JsonOptions.Options) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void Save(CloudPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences, JsonOptions.Options)); File.Move(temp, _path, true);
    }
}

public sealed class CloudPreferences
{
    public bool AutoSyncEnabled { get; set; }
    public HashSet<MapFeatureType> EnabledFeatureTypes { get; set; } = Enum.GetValues<MapFeatureType>().ToHashSet();
}
