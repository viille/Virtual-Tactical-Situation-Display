using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

/// <summary>Conservatively associates simulator objects with already-known direct TacticalLink tracks.</summary>
public sealed class TrafficDeduplicator
{
    private readonly Dictionary<string, string> _associations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _confirmationCounts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TrafficContactState> SuppressDuplicates(
        IReadOnlyList<TrafficContactState> simulatorContacts,
        IReadOnlyList<TrafficContactState> tacticalContacts)
    {
        var claimedPeers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TrafficContactState>(simulatorContacts.Count);
        foreach (var contact in simulatorContacts.OrderByDescending(contact => _associations.ContainsKey(contact.Id)))
        {
            var candidates = tacticalContacts
                .Where(peer => !claimedPeers.ContainsKey(peer.Id) || claimedPeers[peer.Id] == contact.Id)
                .Select(peer => (Peer: peer, Score: MatchScore(contact, peer)))
                .Where(candidate => candidate.Score <= 0.10)
                .OrderBy(candidate => candidate.Score)
                .ToArray();
            var previousPeerId = _associations.GetValueOrDefault(contact.Id);
            var match = candidates.FirstOrDefault(candidate => candidate.Peer.Id == previousPeerId);
            if (match.Peer is null) match = candidates.FirstOrDefault();
            if (match.Peer is null)
            {
                _associations.Remove(contact.Id);
                _confirmationCounts.Remove(contact.Id);
                result.Add(contact);
                continue;
            }

            var previous = _associations.GetValueOrDefault(contact.Id);
            if (previous == match.Peer.Id) _confirmationCounts[contact.Id] = _confirmationCounts.GetValueOrDefault(contact.Id) + 1;
            else { _associations[contact.Id] = match.Peer.Id; _confirmationCounts[contact.Id] = 1; }
            claimedPeers[match.Peer.Id] = contact.Id;
            if (_confirmationCounts[contact.Id] < 3) result.Add(contact);
        }

        var active = simulatorContacts.Select(contact => contact.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _associations.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _associations.Remove(id);
            _confirmationCounts.Remove(id);
        }
        return result;
    }

    private static double MatchScore(TrafficContactState simulator, TrafficContactState direct)
    {
        var deltaSeconds = (direct.Timestamp - simulator.Timestamp).TotalSeconds;
        var ageSeconds = System.Math.Abs(deltaSeconds);
        if (ageSeconds > 1.0) return double.PositiveInfinity;
        var simulatorPosition = PositionAt(simulator, System.Math.Max(0, deltaSeconds));
        var directPosition = PositionAt(direct, System.Math.Max(0, -deltaSeconds));
        var distanceNm = GeoMath.DistanceNm(simulatorPosition.Lat, simulatorPosition.Lon, directPosition.Lat, directPosition.Lon);
        var altitudeThousandsFt = System.Math.Abs(simulator.AltitudeFt - direct.AltitudeFt) / 1000.0;
        var velocityMismatch = VelocityMismatch(simulator, direct);
        if (velocityMismatch > 75) return double.PositiveInfinity;
        if (ageSeconds > 0.2 && velocityMismatch > 45) return double.PositiveInfinity;
        var speedHundredsKt = simulator.SpeedKt.HasValue && direct.SpeedKt.HasValue
            ? System.Math.Abs(simulator.SpeedKt.Value - direct.SpeedKt.Value) / 100.0 : 0.25;
        var headingFraction = simulator.HeadingDeg.HasValue && direct.HeadingDeg.HasValue
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(simulator.HeadingDeg.Value, direct.HeadingDeg.Value)) / 180.0 : 0.25;
        if (distanceNm > 0.35 || altitudeThousandsFt > 0.5) return double.PositiveInfinity;
        var motionFraction = double.IsFinite(velocityMismatch) ? velocityMismatch / 75 : 0.25;
        return distanceNm / 0.35 * 0.55 + altitudeThousandsFt / 0.5 * 0.2 + speedHundredsKt * 0.15 + headingFraction * 0.1 + ageSeconds * 0.02 + motionFraction * 0.03;
    }

    private static (double Lat, double Lon) PositionAt(TrafficContactState contact, double seconds)
    {
        var north = contact.VelocityNorthMps;
        var east = contact.VelocityEastMps;
        if ((!north.HasValue || !east.HasValue) && contact.SpeedKt is { } speed)
        {
            var track = ((contact.GroundTrackDeg ?? contact.HeadingDeg) ?? 0) * (System.Math.PI / 180.0);
            var mps = speed * 0.514444;
            north = mps * System.Math.Cos(track);
            east = mps * System.Math.Sin(track);
        }
        if (!north.HasValue || !east.HasValue || seconds == 0) return (contact.LatitudeDeg, contact.LongitudeDeg);
        var lat = contact.LatitudeDeg + north.Value * seconds / 111_320.0;
        var cosLat = System.Math.Max(0.01, System.Math.Abs(System.Math.Cos(contact.LatitudeDeg * System.Math.PI / 180.0)));
        var lon = contact.LongitudeDeg + east.Value * seconds / (111_320.0 * cosLat);
        return (lat, lon);
    }

    private static double VelocityMismatch(TrafficContactState first, TrafficContactState second)
    {
        var a = Velocity(first);
        var b = Velocity(second);
        if (a is null || b is null) return double.NaN;
        var dn = a.Value.N - b.Value.N;
        var de = a.Value.E - b.Value.E;
        var dd = a.Value.D - b.Value.D;
        return System.Math.Sqrt(dn * dn + de * de + dd * dd) * 1.94384;
    }

    private static (double N, double E, double D)? Velocity(TrafficContactState contact)
    {
        if (contact.VelocityNorthMps is { } n && contact.VelocityEastMps is { } e)
            return (n, e, contact.VelocityDownMps ?? 0);
        if (contact.SpeedKt is not { } speed || (contact.GroundTrackDeg ?? contact.HeadingDeg) is not { } track) return null;
        var radians = track * System.Math.PI / 180.0;
        var mps = speed * 0.514444;
        return (mps * System.Math.Cos(radians), mps * System.Math.Sin(radians), 0);
    }
}
