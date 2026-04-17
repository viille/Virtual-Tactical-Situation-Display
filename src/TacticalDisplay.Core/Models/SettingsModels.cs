namespace TacticalDisplay.Core.Models;

public sealed class TacticalDisplaySettings
{
    public string DataSourceMode { get; set; } = "Demo";
    public string? MsfsExePath { get; set; }
    public string? PreferredSimConnectDllPath { get; set; }
    public string XPlane12ApiBaseUrl { get; set; } = "http://localhost:8086/";
    public bool EnableWebServer { get; set; } = true;
    public bool EnableDataSourceDebugLogging { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 840;
    public double MinTrackedAltitudeFt { get; set; } = 200;
    public int[] RangeScaleOptionsNm { get; init; } = [10, 20, 40, 80, 120];
    public int SelectedRangeNm { get; set; } = 40;
    public ScopeOrientationMode OrientationMode { get; set; } = ScopeOrientationMode.HeadingUp;
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
    public string AirspaceDataBaseUrl { get; set; } = "https://raw.githubusercontent.com/ottotuhkunen/virtual-lara-airspace-data/main/data";
    public string AirspaceActivationUrl { get; set; } = "https://lara-backend.lusep.fi/data/reservations/efin.json";
    public bool ShowBullseye { get; set; }
    public double? BullseyeLatitudeDeg { get; set; }
    public double? BullseyeLongitudeDeg { get; set; }
    public bool Declutter { get; set; }
    public bool TrailsEnabled { get; set; } = true;
    public LabelMode LabelMode { get; set; } = LabelMode.Minimal;
    public RangeFilterMode RangeFilter { get; set; } = RangeFilterMode.All;
    public AltitudeFilterMode AltitudeFilter { get; set; } = AltitudeFilterMode.All;
    public CategoryFilterMode CategoryFilter { get; set; } = CategoryFilterMode.All;
    public int TrailLengthSamples { get; set; } = 90;
    public double PollRateHz { get; set; } = 8;
    public double RenderRateFps { get; set; } = 24;
    public double StaleSeconds { get; set; } = 4;
    public double RemoveAfterSeconds { get; set; } = 12;
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
