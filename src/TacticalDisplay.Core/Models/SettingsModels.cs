namespace TacticalDisplay.Core.Models;

public sealed class TacticalDisplaySettings
{
    public string DataSourceMode { get; set; } = "Demo";
    public string? MsfsExePath { get; set; }
    public string? PreferredSimConnectDllPath { get; set; }
    public int[] RangeScaleOptionsNm { get; init; } = [10, 20, 40, 80, 120];
    public int SelectedRangeNm { get; set; } = 40;
    public ScopeOrientationMode OrientationMode { get; set; } = ScopeOrientationMode.HeadingUp;
    public bool ShowRangeRings { get; set; } = true;
    public bool Declutter { get; set; }
    public bool TrailsEnabled { get; set; } = true;
    public bool ShowDirectCallsigns { get; set; } = true;
    public LabelMode LabelMode { get; set; } = LabelMode.Minimal;
    public RangeFilterMode RangeFilter { get; set; } = RangeFilterMode.All;
    public AltitudeFilterMode AltitudeFilter { get; set; } = AltitudeFilterMode.All;
    public CategoryFilterMode CategoryFilter { get; set; } = CategoryFilterMode.All;
    public int TrailLengthSamples { get; set; } = 15;
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
    public bool VatsimLikeTrafficSeen { get; set; }
    public int TrafficCount { get; set; }
    public double ActualPollRateHz { get; set; }
}
