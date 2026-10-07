using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

/// <summary>Combines direct TacticalLink tracks with nonduplicate simulator contacts.</summary>
public sealed class TrafficFusion
{
    private readonly TrafficDeduplicator _deduplicator = new();
    private readonly HashSet<string> _previousDirectTrackIds = new(StringComparer.Ordinal);

    public TrafficFusionResult Fuse(TrafficSnapshot simulator, IReadOnlyList<TacticalPeer> peers)
    {
        var direct = peers
            .Where(peer => peer.LatestTelemetry is not null)
            .Select(peer => ToTrafficContact(peer, peer.LatestTelemetry!))
            .ToArray();
        var directIds = direct.Select(contact => contact.Id).ToHashSet(StringComparer.Ordinal);
        var removed = _previousDirectTrackIds.Except(directIds, StringComparer.Ordinal).ToArray();
        _previousDirectTrackIds.Clear();
        _previousDirectTrackIds.UnionWith(directIds);

        var unmatchedSimulator = _deduplicator.SuppressDuplicates(simulator.Contacts, direct);
        return new TrafficFusionResult(unmatchedSimulator.Concat(direct).ToArray(), removed);
    }

    private static TrafficContactState ToTrafficContact(TacticalPeer peer, TacticalTelemetry telemetry) => new(
        "tactical:" + peer.ParticipantId,
        peer.Callsign,
        telemetry.LatitudeDeg,
        telemetry.LongitudeDeg,
        telemetry.AltitudeFt,
        telemetry.HeadingDeg,
        telemetry.SpeedKt,
        telemetry.SampleTimestampUtc,
        Generation: 1,
        GroundTrackDeg: telemetry.GroundTrackDeg,
        Source: TrackSource.TacticalLink,
        VelocityNorthMps: telemetry.VelocityNorthMps,
        VelocityEastMps: telemetry.VelocityEastMps,
        VelocityDownMps: telemetry.VelocityDownMps,
        AircraftType: peer.AircraftType);
}

public sealed record TrafficFusionResult(
    IReadOnlyList<TrafficContactState> Contacts,
    IReadOnlyList<string> RemovedDirectTrackIds);
