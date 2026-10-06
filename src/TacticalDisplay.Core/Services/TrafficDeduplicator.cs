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
        var ageSeconds = System.Math.Abs((simulator.Timestamp - direct.Timestamp).TotalSeconds);
        if (ageSeconds > 2.0) return double.PositiveInfinity;
        var distanceNm = GeoMath.DistanceNm(simulator.LatitudeDeg, simulator.LongitudeDeg, direct.LatitudeDeg, direct.LongitudeDeg);
        var altitudeThousandsFt = System.Math.Abs(simulator.AltitudeFt - direct.AltitudeFt) / 1000.0;
        var speedHundredsKt = simulator.SpeedKt.HasValue && direct.SpeedKt.HasValue
            ? System.Math.Abs(simulator.SpeedKt.Value - direct.SpeedKt.Value) / 100.0 : 0.25;
        var headingFraction = simulator.HeadingDeg.HasValue && direct.HeadingDeg.HasValue
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(simulator.HeadingDeg.Value, direct.HeadingDeg.Value)) / 180.0 : 0.25;
        if (distanceNm > 0.35 || altitudeThousandsFt > 0.5) return double.PositiveInfinity;
        return distanceNm / 0.35 * 0.55 + altitudeThousandsFt / 0.5 * 0.2 + speedHundredsKt * 0.15 + headingFraction * 0.1 + ageSeconds / 2 * 0.1;
    }
}
