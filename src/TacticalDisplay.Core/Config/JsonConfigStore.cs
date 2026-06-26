using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Config;

public sealed class JsonConfigStore
{
    private const string CorruptTimestampFormat = "yyyyMMddHHmmss";

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
            BackupCorruptFile(path);
            var defaults = new TacticalDisplaySettings();
            SaveDisplaySettings(defaults);
            return defaults;
        }
    }

    public void SaveDisplaySettings(TacticalDisplaySettings settings)
    {
        var path = Path.Combine(ConfigDirectory, "display.json");
        WriteAllTextAtomic(path, JsonSerializer.Serialize(settings, _jsonOptions));
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
            BackupCorruptFile(path);
            WriteAllTextAtomic(path, "{}");
            return [];
        }
    }

    public void SaveManualTargetMetadata(Dictionary<string, ManualTargetMetadata> metadata)
    {
        var path = Path.Combine(ConfigDirectory, "manual-targets.json");
        WriteAllTextAtomic(path, JsonSerializer.Serialize(metadata, _jsonOptions));
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
            BackupCorruptFile(path);
            WriteAllTextAtomic(path, "[]");
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
        settings.VatsimCallsignRefreshSeconds = System.Math.Clamp(settings.VatsimCallsignRefreshSeconds, 15, 300);

        if (settings.TrailLengthSamples == 15)
        {
            settings.TrailLengthSamples = 90;
        }

        if (string.Equals(settings.AirspaceActivationUrl, "https://lara-backend.lusep.fi/topsky/lara.txt", StringComparison.OrdinalIgnoreCase))
        {
            settings.AirspaceActivationUrl = "https://lara-backend.lusep.fi/data/reservations/efin.json";
        }

        settings.KneepadContentMode = NormalizeKneepadContentMode(settings.KneepadContentMode);
        settings.KneepadMissionInformation ??= string.Empty;
        settings.KneepadImagePath ??= string.Empty;
        settings.KneepadUrl ??= string.Empty;
        if (settings.KneepadPages.Count == 0)
        {
            settings.KneepadPages.Add(new KneepadPage
            {
                ContentMode = string.IsNullOrWhiteSpace(settings.KneepadMissionInformation) &&
                    string.IsNullOrWhiteSpace(settings.KneepadImagePath) &&
                    string.IsNullOrWhiteSpace(settings.KneepadUrl)
                        ? "Empty"
                        : settings.KneepadContentMode,
                MissionInformation = settings.KneepadMissionInformation,
                ImagePath = settings.KneepadImagePath,
                Url = settings.KneepadUrl
            });
        }

        foreach (var page in settings.KneepadPages)
        {
            page.ContentMode = NormalizeKneepadContentMode(page.ContentMode);
            page.MissionInformation ??= string.Empty;
            page.ImagePath ??= string.Empty;
            page.Url ??= string.Empty;
        }

        settings.SelectedKneepadPageIndex = System.Math.Clamp(settings.SelectedKneepadPageIndex, 0, settings.KneepadPages.Count - 1);

        settings.Hotkeys ??= [];
        foreach (var defaultBinding in HotkeyDefaults.CreateDefaultBindings())
        {
            var existing = settings.Hotkeys.FirstOrDefault(binding =>
                string.Equals(binding.Action, defaultBinding.Action, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.Hotkeys.Add(defaultBinding);
                continue;
            }

            existing.Action = defaultBinding.Action;
            existing.Keyboard ??= string.Empty;
            existing.Gamepad ??= string.Empty;
        }

        settings.Hotkeys = settings.Hotkeys
            .Where(static binding => HotkeyDefaults.Actions.Any(action =>
                string.Equals(action.Action, binding.Action, StringComparison.OrdinalIgnoreCase)))
            .ToList();
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
            !Enum.IsDefined(settings.DirectionReferenceMode) ||
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
            !IsPositive(settings.MaxTrackedAltitudeFt) ||
            settings.MaxTrackedAltitudeFt < settings.MinTrackedAltitudeFt ||
            !IsPositive(settings.WindowWidth) ||
            !IsPositive(settings.WindowHeight) ||
            !IsPositive(settings.TargetSymbolScale) ||
            !IsPositive(settings.VatsimCallsignRefreshSeconds) ||
            settings.TrailLengthSamples <= 0)
        {
            throw new InvalidDataException("Display settings contain invalid timing or filter values.");
        }

        if (string.IsNullOrWhiteSpace(settings.XPlane12ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(settings.VatsimDataFeedUrl) ||
            string.IsNullOrWhiteSpace(settings.AirspaceFirCode) ||
            string.IsNullOrWhiteSpace(settings.AirspaceDataBaseUrl) ||
            !settings.KneepadPages.All(static page => IsValidKneepadContentMode(page.ContentMode)) ||
            !settings.Hotkeys.All(static binding => !string.IsNullOrWhiteSpace(binding.Action)))
        {
            throw new InvalidDataException("Display settings contain invalid URL or FIR values.");
        }
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static bool IsValidKneepadContentMode(string mode) =>
        string.Equals(mode, "Empty", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Mission", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Url", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKneepadContentMode(string? mode) =>
        string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase) ? "Image" :
        string.Equals(mode, "Url", StringComparison.OrdinalIgnoreCase) ? "Url" :
        string.Equals(mode, "Mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
        string.Equals(mode, "Empty", StringComparison.OrdinalIgnoreCase) ? "Empty" :
        "Mission";

    private static void BackupCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToString(CorruptTimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = $"{path}.corrupt-{timestamp}";
        var attempt = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{path}.corrupt-{timestamp}-{attempt}";
            attempt++;
        }

        File.Move(path, backupPath);
    }

    private static void WriteAllTextAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }
}
