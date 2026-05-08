using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public static class VatsimCallsignMatcher
{
    private const double MaxMatchDistanceNm = 3.0;
    private const double MaxMatchAltitudeFt = 1500;

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

    public static VatsimMatchDiagnostics InspectBestMatch(
        TrafficContactState contact,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        VatsimMatchDiagnostics? best = null;
        for (var i = 0; i < pilots.Count; i++)
        {
            var pilot = pilots[i];
            var candidate = InspectCandidate(contact, pilot);
            if (best is null || candidate.Score < best.Score)
            {
                best = candidate;
            }
        }

        return best ?? VatsimMatchDiagnostics.None;
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
        var diagnostics = InspectCandidate(contact, pilot);
        score = diagnostics.Score;
        return diagnostics.IsMatch;
    }

    private static VatsimMatchDiagnostics InspectCandidate(TrafficContactState contact, VatsimPilotCandidate pilot)
    {
        var distanceNm = GeoMath.DistanceNm(contact.LatitudeDeg, contact.LongitudeDeg, pilot.LatitudeDeg, pilot.LongitudeDeg);
        var altitudeDeltaFt = System.Math.Abs(contact.AltitudeFt - pilot.AltitudeFt);
        var headingPenalty = contact.HeadingDeg.HasValue
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(contact.HeadingDeg.Value, pilot.HeadingDeg))
            : 0;
        var speedPenalty = contact.SpeedKt.HasValue
            ? System.Math.Abs(contact.SpeedKt.Value - pilot.GroundspeedKt)
            : 0;
        var score = distanceNm + altitudeDeltaFt / 1000.0 + headingPenalty / 180.0 + speedPenalty / 360.0;

        if (distanceNm > MaxMatchDistanceNm)
        {
            return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "distance");
        }

        if (altitudeDeltaFt > MaxMatchAltitudeFt)
        {
            return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "altitude");
        }

        return new VatsimMatchDiagnostics(true, pilot.Callsign, distanceNm, altitudeDeltaFt, score, null);
    }
}

public sealed record VatsimPilotCandidate(
    string Callsign,
    double LatitudeDeg,
    double LongitudeDeg,
    int AltitudeFt,
    int GroundspeedKt,
    int HeadingDeg);

public sealed record VatsimMatchDiagnostics(
    bool IsMatch,
    string? Callsign,
    double DistanceNm,
    double AltitudeDeltaFt,
    double Score,
    string? RejectReason)
{
    public static VatsimMatchDiagnostics None { get; } = new(false, null, 0, 0, double.MaxValue, "no-pilots");
}
