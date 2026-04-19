namespace TacticalDisplay.Core.Models;

public enum TargetCategory
{
    Own,
    Friend,
    Enemy,
    Package,
    Support,
    Unknown,
    Stale
}

public enum ScopeOrientationMode
{
    NorthUp,
    HeadingUp
}

public enum LabelMode
{
    Full,
    Minimal,
    Off
}

public enum CategoryFilterMode
{
    All,
    FriendlyOnly,
    PackageOnly,
    UnknownOnly
}

public sealed record OwnshipState(
    string Id,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeFt,
    double HeadingDeg,
    double? SpeedKt,
    DateTimeOffset Timestamp);

public sealed record TrafficContactState(
    string Id,
    string? Callsign,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeFt,
    double? HeadingDeg,
    double? SpeedKt,
    DateTimeOffset Timestamp);

public sealed record PositionHistoryPoint(
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeFt,
    DateTimeOffset Timestamp);

public sealed record TrafficSnapshot(
    OwnshipState Ownship,
    IReadOnlyList<TrafficContactState> Contacts,
    DateTimeOffset Timestamp);

public sealed record ComputedTarget(
    string Id,
    string DisplayName,
    TargetCategory Category,
    bool IsStale,
    double RangeNm,
    double BearingDegTrue,
    double RelativeBearingDeg,
    double RelativeAltitudeFt,
    double? HeadingDeg,
    double? SpeedKt,
    double? ClosureKt,
    IReadOnlyList<PositionHistoryPoint> History,
    DateTimeOffset Timestamp);

public sealed record TacticalPicture(
    OwnshipState Ownship,
    IReadOnlyList<ComputedTarget> Targets,
    DateTimeOffset Timestamp);

public sealed record AirspaceArea(
    string Name,
    string Type,
    int? LowerFlightLevel,
    int? UpperFlightLevel,
    bool IsActive,
    int? ActiveLowerAltitudeFt,
    int? ActiveUpperAltitudeFt,
    IReadOnlyList<AirspacePolygon> Polygons);

public sealed record AirspacePolygon(
    IReadOnlyList<AirspaceCoordinate> Exterior);

public sealed record AirspaceCoordinate(
    double LatitudeDeg,
    double LongitudeDeg);
