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

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<TacticalDisplaySettings>(json, _jsonOptions) ?? new TacticalDisplaySettings();
            ApplyDisplaySettingMigrations(settings);
            ValidateDisplaySettings(settings);
            return settings;
        }
        catch (Exception)
        {
            var defaults = new TacticalDisplaySettings();
            SaveDisplaySettings(defaults);
            return defaults;
        }
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

        try
        {
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, ManualTargetMetadata>>(json, _jsonOptions) ?? [];
            return new Dictionary<string, ManualTargetMetadata>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            File.WriteAllText(path, "{}");
            return [];
        }
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

        List<string> list;
        try
        {
            var json = File.ReadAllText(path);
            list = JsonSerializer.Deserialize<List<string>>(json, _jsonOptions) ?? [];
        }
        catch (Exception)
        {
            File.WriteAllText(path, "[]");
            list = [];
        }

        return list
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyDisplaySettingMigrations(TacticalDisplaySettings settings)
    {
        settings.AirspaceOpacity = System.Math.Clamp(settings.AirspaceOpacity, 0.1, 1.0);
        settings.MapOpacity = System.Math.Clamp(settings.MapOpacity, 0.0, 1.0);
        settings.MapLabelBackgroundOpacity = System.Math.Clamp(settings.MapLabelBackgroundOpacity, 0.0, 1.0);
        settings.TargetSymbolScale = System.Math.Clamp(settings.TargetSymbolScale, 0.6, 1.8);
        settings.WindowWidth = System.Math.Clamp(settings.WindowWidth, 640, 3840);
        settings.WindowHeight = System.Math.Clamp(settings.WindowHeight, 480, 2160);

        if (settings.TrailLengthSamples == 15)
        {
            settings.TrailLengthSamples = 90;
        }

        if (string.Equals(settings.AirspaceActivationUrl, "https://lara-backend.lusep.fi/topsky/lara.txt", StringComparison.OrdinalIgnoreCase))
        {
            settings.AirspaceActivationUrl = "https://lara-backend.lusep.fi/data/reservations/efin.json";
        }
    }

    private static void ValidateDisplaySettings(TacticalDisplaySettings settings)
    {
        if (settings.RangeScaleOptionsNm.Length == 0 ||
            settings.RangeScaleOptionsNm.Any(range => range <= 0) ||
            !settings.RangeScaleOptionsNm.Contains(settings.SelectedRangeNm))
        {
            throw new InvalidDataException("Display settings contain an invalid range scale.");
        }

        if (!Enum.IsDefined(settings.OrientationMode) ||
            !Enum.IsDefined(settings.LabelMode) ||
            !Enum.IsDefined(settings.CategoryFilter))
        {
            throw new InvalidDataException("Display settings contain an invalid enum value.");
        }

        if (!IsPositive(settings.PollRateHz) ||
            !IsPositive(settings.RenderRateFps) ||
            !IsPositive(settings.StaleSeconds) ||
            !IsPositive(settings.RemoveAfterSeconds) ||
            !IsFiniteNonNegative(settings.MinTrackedAltitudeFt) ||
            !IsPositive(settings.WindowWidth) ||
            !IsPositive(settings.WindowHeight) ||
            !IsPositive(settings.TargetSymbolScale) ||
            settings.TrailLengthSamples <= 0)
        {
            throw new InvalidDataException("Display settings contain invalid timing or filter values.");
        }

        if (string.IsNullOrWhiteSpace(settings.XPlane12ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(settings.AirspaceFirCode) ||
            string.IsNullOrWhiteSpace(settings.AirspaceDataBaseUrl))
        {
            throw new InvalidDataException("Display settings contain invalid URL or FIR values.");
        }
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
}
