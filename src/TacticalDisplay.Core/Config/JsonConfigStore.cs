using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Config;

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
        return new ClassificationConfig
        {
            FriendCallsigns = LoadCallsignSet("friends.json"),
            PackageCallsigns = LoadCallsignSet("package.json"),
            SupportCallsigns = LoadCallsignSet("support.json")
        };
    }

    public Dictionary<string, ManualTargetMetadata> LoadManualTargetMetadata()
    {
        var path = Path.Combine(ConfigDirectory, "manual-targets.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "{}");
        }

        var json = File.ReadAllText(path);
        var map = JsonSerializer.Deserialize<Dictionary<string, ManualTargetMetadata>>(json, _jsonOptions) ?? [];
        return new Dictionary<string, ManualTargetMetadata>(map, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveManualTargetMetadata(Dictionary<string, ManualTargetMetadata> metadata)
    {
        var path = Path.Combine(ConfigDirectory, "manual-targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, _jsonOptions));
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
