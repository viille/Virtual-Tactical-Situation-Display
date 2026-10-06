using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public interface IPeerInterestResolver
{
    IReadOnlyList<string> Resolve(string participantId, IReadOnlyDictionary<string, TacticalTelemetry> positions, double radiusNm);
}

public sealed class PeerInterestResolver : IPeerInterestResolver
{
    public IReadOnlyList<string> Resolve(string participantId, IReadOnlyDictionary<string, TacticalTelemetry> positions, double radiusNm)
    {
        if (!positions.TryGetValue(participantId, out var origin)) return [];
        var radius = System.Math.Clamp(radiusNm, 1, 500);
        return positions
            .Where(pair => !string.Equals(pair.Key, participantId, StringComparison.Ordinal))
            .Where(pair => GeoMath.DistanceNm(origin.LatitudeDeg, origin.LongitudeDeg, pair.Value.LatitudeDeg, pair.Value.LongitudeDeg) <= radius)
            .Select(pair => pair.Key)
            .ToArray();
    }
}
