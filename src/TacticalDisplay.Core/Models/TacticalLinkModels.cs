namespace TacticalDisplay.Core.Models;

public static class TacticalLinkProtocol
{
    public const int Version = 1;
}

public enum TacticalLinkConnectionState { Disconnected, Connecting, Connected, Degraded }
public enum TankerAvailability { Off, Available, Busy, Unavailable }
public enum TrackSource { TacticalLink, SimConnectVatsim, SimConnect }

public sealed record TacticalTelemetry(
    long Sequence,
    DateTimeOffset SampleTimestampUtc,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeFt,
    double? HeadingDeg,
    double? GroundTrackDeg,
    double? SpeedKt,
    double? PitchDeg = null,
    double? BankDeg = null,
    double? VerticalSpeedFpm = null,
    double? VelocityNorthMps = null,
    double? VelocityEastMps = null,
    double? VelocityDownMps = null);

public sealed record TacticalPeer(
    string ParticipantId,
    string Callsign,
    string? AircraftType,
    IReadOnlySet<string> Capabilities,
    IReadOnlyDictionary<string, string> OperationalStates,
    TacticalTelemetry? LatestTelemetry,
    TimeSpan? TelemetryAge,
    TacticalLinkConnectionState ConnectionState = TacticalLinkConnectionState.Connected)
{
    public TimeSpan? CurrentTelemetryAge => LatestTelemetry is { } telemetry
        ? DateTimeOffset.UtcNow - telemetry.SampleTimestampUtc
        : null;
    public string StatusText => Capabilities.Contains("aar.tanker") &&
        OperationalStates.TryGetValue("tankerAvailability", out var availability) && availability == "Available"
        ? "AAR AVAILABLE"
        : CurrentTelemetryAge is { } age ? $"Updated {System.Math.Max(0, age.TotalSeconds):0}s ago" : "Telemetry unavailable";
}

public sealed record TacticalLinkState(
    TacticalLinkConnectionState ConnectionState,
    string? LocalParticipantId,
    string? LocalCallsign,
    IReadOnlyList<TacticalPeer> NearbyPeers)
{
    public static TacticalLinkState Disconnected { get; } = new(TacticalLinkConnectionState.Disconnected, null, null, []);
}
