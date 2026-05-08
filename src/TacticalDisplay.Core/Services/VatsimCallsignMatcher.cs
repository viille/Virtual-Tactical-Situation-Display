using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public static class VatsimCallsignMatcher
{
    private const double MaxMatchDistanceNm = 1.5;
    private const double MaxMatchAltitudeFt = 1000;
    private const double MaxMatchHeadingDeg = 45;
    private const double MaxMatchSpeedKt = 90;

    public static TrafficSnapshot EnrichSnapshot(
        TrafficSnapshot snapshot,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        if (pilots.Count == 0 || snapshot.Contacts.Count == 0)
        {
            return snapshot;
        }

        var usedPilotIndexes = new HashSet<int>();
        var contacts = new List<TrafficContactState>(snapshot.Contacts.Count);
        foreach (var contact in snapshot.Contacts)
        {
            if (!string.IsNullOrWhiteSpace(contact.Callsign))
            {
                contacts.Add(contact);
                continue;
            }

            var match = FindBestMatch(contact, pilots, usedPilotIndexes);
            if (match is null)
            {
                contacts.Add(contact);
                continue;
            }

            usedPilotIndexes.Add(match.Value.Index);
            contacts.Add(contact with { Callsign = match.Value.Pilot.Callsign.Trim().ToUpperInvariant() });
        }

        return snapshot with { Contacts = contacts };
    }

    private static (int Index, VatsimPilotCandidate Pilot)? FindBestMatch(
        TrafficContactState contact,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlySet<int> usedPilotIndexes)
    {
        (int Index, VatsimPilotCandidate Pilot, double Score)? best = null;
        for (var i = 0; i < pilots.Count; i++)
        {
            if (usedPilotIndexes.Contains(i))
            {
                continue;
            }

            var pilot = pilots[i];
            if (!IsCandidate(contact, pilot, out var score))
            {
                continue;
            }

            if (best is null || score < best.Value.Score)
            {
                best = (i, pilot, score);
            }
        }

        return best is null ? null : (best.Value.Index, best.Value.Pilot);
    }

    private static bool IsCandidate(TrafficContactState contact, VatsimPilotCandidate pilot, out double score)
    {
        score = double.MaxValue;
        var distanceNm = GeoMath.DistanceNm(contact.LatitudeDeg, contact.LongitudeDeg, pilot.LatitudeDeg, pilot.LongitudeDeg);
        if (distanceNm > MaxMatchDistanceNm)
        {
            return false;
        }

        var altitudeDeltaFt = System.Math.Abs(contact.AltitudeFt - pilot.AltitudeFt);
        if (altitudeDeltaFt > MaxMatchAltitudeFt)
        {
            return false;
        }

        var headingDeltaDeg = contact.HeadingDeg.HasValue
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(contact.HeadingDeg.Value, pilot.HeadingDeg))
            : 0;
        if (headingDeltaDeg > MaxMatchHeadingDeg)
        {
            return false;
        }

        var speedDeltaKt = contact.SpeedKt.HasValue
            ? System.Math.Abs(contact.SpeedKt.Value - pilot.GroundspeedKt)
            : 0;
        if (speedDeltaKt > MaxMatchSpeedKt)
        {
            return false;
        }

        score = distanceNm + altitudeDeltaFt / 1000.0 + headingDeltaDeg / 90.0 + speedDeltaKt / 180.0;
        return true;
    }
}

public sealed record VatsimPilotCandidate(
    string Callsign,
    double LatitudeDeg,
    double LongitudeDeg,
    int AltitudeFt,
    int GroundspeedKt,
    int HeadingDeg);
