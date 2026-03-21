using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Config;

public sealed class JsonConfigStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string ConfigDirectory { get; }

    public JsonConfigStore(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        Directory.CreateDirectory(ConfigDirectory);
    }

    public TacticalDisplaySettings LoadDisplaySettings()
    {
        var path = Path.Combine(ConfigDirectory, "display.json");
        if (!File.Exists(path))
        {
            var defaults = new TacticalDisplaySettings();
            SaveDisplaySettings(defaults);
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TacticalDisplaySettings>(json, _jsonOptions) ?? new TacticalDisplaySettings();
    }

    public void SaveDisplaySettings(TacticalDisplaySettings settings)
    {
        var path = Path.Combine(ConfigDirectory, "display.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, _jsonOptions));
    }

    public ClassificationConfig LoadClassification()
    {
        var config = new ClassificationConfig
        {
            FriendCallsigns = LoadCallsignSet("friends.json"),
            PackageCallsigns = LoadCallsignSet("package.json"),
            SupportCallsigns = LoadCallsignSet("support.json")
        };
        return config;
    }

    private HashSet<string> LoadCallsignSet(string fileName)
    {
        var path = Path.Combine(ConfigDirectory, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "[]");
        }

        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<string>>(json, _jsonOptions) ?? [];
        return list
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
