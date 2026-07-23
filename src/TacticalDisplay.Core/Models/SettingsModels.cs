namespace TacticalDisplay.Core.Models;

public sealed class TacticalDisplaySettings
{
    public string DataSourceMode { get; set; } = "Demo";
    public string? MsfsExePath { get; set; }
    public string? PreferredSimConnectDllPath { get; set; }
    public string XPlane12ApiBaseUrl { get; set; } = "http://localhost:8086/";
    public bool EnableWebServer { get; set; } = true;
    public bool EnableWebServerLanAccess { get; set; } = true;
    public bool EnableVatsimCallsignLookup { get; set; } = true;
    public string VatsimDataFeedUrl { get; set; } = "https://data.vatsim.net/v3/vatsim-data.json";
    public double VatsimCallsignRefreshSeconds { get; set; } = 15;
    public bool EnableDiagnosticTelemetry { get; set; }
    public bool DiagnosticTelemetryConsentAsked { get; set; }
    public bool EnableDataSourceDebugLogging { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 840;
    public double MinTrackedAltitudeFt { get; set; } = 200;
    public double MaxTrackedAltitudeFt { get; set; } = 80000;
    public int[] RangeScaleOptionsNm { get; init; } = [10, 20, 40, 80, 120];
    public int SelectedRangeNm { get; set; } = 40;
    public ScopeOrientationMode OrientationMode { get; set; } = ScopeOrientationMode.HeadingUp;
    public DirectionReferenceMode DirectionReferenceMode { get; set; } = DirectionReferenceMode.True;
    public bool ShowMapLayer { get; set; } = true;
    public double MapOpacity { get; set; } = 0.65;
    public double MapLabelBackgroundOpacity { get; set; } = 0.75;
    public bool ShowRangeRings { get; set; } = true;
    public double TargetSymbolScale { get; set; } = 1.0;
    public bool ShowAirspaceBoundaries { get; set; } = true;
    public bool ShowControlledAirspaceLayer { get; set; }
    public bool ShowOnlyActiveAirspaceBoundaries { get; set; } = true;
    public double AirspaceOpacity { get; set; } = 1.0;
    public string AirspaceFirCode { get; set; } = "efin";
    public string[] AirspaceFirCodes { get; set; } = ["efin", "eett"];
    public string AirspaceDataBaseUrl { get; set; } = "https://raw.githubusercontent.com/ottotuhkunen/virtual-lara-airspace-data/main/data";
    public string AirspaceActivationUrl { get; set; } = "https://lara-backend.lusep.fi/data/reservations/efin.json";
    public string[] AirspaceActivationUrls { get; set; } =
    [
        "https://lara-backend.lusep.fi/data/reservations/efin.json",
        "https://lara-backend.lusep.fi/data/reservations/eett.json"
    ];
    public bool ShowBullseye { get; set; }
    public double? BullseyeLatitudeDeg { get; set; }
    public double? BullseyeLongitudeDeg { get; set; }
    public string KneepadContentMode { get; set; } = "Mission";
    public string KneepadMissionInformation { get; set; } = string.Empty;
    public string KneepadImagePath { get; set; } = string.Empty;
    public string KneepadUrl { get; set; } = string.Empty;
    public List<KneepadPage> KneepadPages { get; set; } = [];
    public int SelectedKneepadPageIndex { get; set; }
    public bool Declutter { get; set; }
    public bool TrailsEnabled { get; set; } = true;
    public LabelMode LabelMode { get; set; } = LabelMode.Minimal;
    public CategoryFilterMode CategoryFilter { get; set; } = CategoryFilterMode.All;
    public int TrailLengthSamples { get; set; } = 90;
    public double PollRateHz { get; set; } = 8;
    public double RenderRateFps { get; set; } = 24;
    public double StaleSeconds { get; set; } = 4;
    public double RemoveAfterSeconds { get; set; } = 12;
    public List<HotkeyBinding> Hotkeys { get; set; } = HotkeyDefaults.CreateDefaultBindings();
}

public sealed class HotkeyBinding
{
    public string Action { get; set; } = string.Empty;
    public string Keyboard { get; set; } = string.Empty;
    public string Gamepad { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public static class HotkeyDefaults
{
    public static readonly IReadOnlyList<HotkeyActionDefinition> Actions =
    [
        new("range-up", "Range up", string.Empty, "DPadUp"),
        new("range-down", "Range down", string.Empty, "DPadDown"),
        new("orientation", "Toggle north/heading up", string.Empty, "Y"),
        new("map", "Toggle map", string.Empty, string.Empty),
        new("declutter", "Toggle declutter", "Ctrl+D", "X"),
        new("trails", "Toggle trails", string.Empty, string.Empty),
        new("bullseye", "Toggle bullseye", string.Empty, string.Empty),
        new("intercept", "Toggle intercept", string.Empty, "A"),
        new("labels", "Cycle labels", string.Empty, "B"),
        new("airspace", "Toggle LARA airspace", string.Empty, string.Empty),
        new("area", "Toggle controlled airspace", string.Empty, string.Empty),
        new("pin", "Pin window on top", "Ctrl+T", string.Empty),
        new("fullscreen", "Toggle fullscreen", "Ctrl+Shift+F", string.Empty),
        new("settings", "Show or hide settings", "Ctrl+H", "Start"),
        new("kneepad", "Show or hide kneepad", "Ctrl+K", "Back"),
        new("kneepad-prev", "Previous kneepad page", "Ctrl+PageUp", "LeftShoulder"),
        new("kneepad-next", "Next kneepad page", "Ctrl+PageDown", "RightShoulder"),
    ];

    public static List<HotkeyBinding> CreateDefaultBindings() =>
        Actions
            .Select(static action => new HotkeyBinding
            {
                Action = action.Action,
                Keyboard = action.Keyboard,
                Gamepad = action.Gamepad,
                IsEnabled = true
            })
            .ToList();
}

public sealed record HotkeyActionDefinition(string Action, string DisplayName, string Keyboard, string Gamepad);

public sealed class KneepadPage
{
    public string ContentMode { get; set; } = "Empty";
    public string MissionInformation { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class ClassificationConfig
{
    public HashSet<string> FriendCallsigns { get; init; } = [];
    public HashSet<string> PackageCallsigns { get; init; } = [];
    public HashSet<string> SupportCallsigns { get; init; } = [];
}

public sealed class RuntimeStatus
{
    public bool SimConnected { get; set; }
    public int TrafficCount { get; set; }
    public double ActualPollRateHz { get; set; }
}
