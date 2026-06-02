using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public static class VatsimCallsignMatcher
{
    private const double MaxMatchDistanceNm = 1.5;
    private const double MaxMatchAltitudeFt = 800;
    private const double MaxMatchHeadingDeltaDeg = 45;
    private const double MaxMatchSpeedDeltaKt = 100;
    private const double MinAirborneSpeedForMotionCheckKt = 40;
    private const double MinBestScoreMargin = 0.75;
    private const double MinPilotBestScoreMargin = 0.75;
    private static readonly TimeSpan MaxHistoricalMatchAge = TimeSpan.FromSeconds(20);

    public static TrafficSnapshot EnrichSnapshot(
        TrafficSnapshot snapshot,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        if (pilots.Count == 0 || snapshot.Contacts.Count == 0)
        {
            return snapshot;
        }

        var unresolvedContacts = snapshot.Contacts
            .Where(static contact => string.IsNullOrWhiteSpace(contact.Callsign))
            .ToList();
        var ambiguousPilotIndexes = FindAmbiguousPilotIndexes(unresolvedContacts, pilots);
        var usedPilotIndexes = new HashSet<int>();
        var contacts = new List<TrafficContactState>(snapshot.Contacts.Count);
        foreach (var contact in snapshot.Contacts)
        {
            if (!string.IsNullOrWhiteSpace(contact.Callsign))
            {
                contacts.Add(contact);
                continue;
            }

            var match = FindBestMatch(contact, pilots, usedPilotIndexes, ambiguousPilotIndexes);
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

    public static TrafficSnapshot EnrichSnapshotFromHistory(
        TrafficSnapshot snapshot,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        if (pilots.Count == 0 || snapshot.Contacts.Count == 0)
        {
            return snapshot;
        }

        var unresolvedContacts = snapshot.Contacts
            .Where(static contact => string.IsNullOrWhiteSpace(contact.Callsign))
            .ToList();
        var ambiguousCurrentPilotIndexes = FindAmbiguousPilotIndexes(unresolvedContacts, pilots);
        var ambiguousHistoricalPilotIndexes = FindAmbiguousHistoricalPilotIndexes(unresolvedContacts, history, pilots);
        var ambiguousPilotIndexes = ambiguousCurrentPilotIndexes
            .Concat(ambiguousHistoricalPilotIndexes)
            .ToHashSet();
        var timestampedPilotIndexes = FindTimestampedPilotIndexes(pilots);
        var usedPilotIndexes = new HashSet<int>();
        var contacts = new List<TrafficContactState>(snapshot.Contacts.Count);
        foreach (var contact in snapshot.Contacts)
        {
            if (!string.IsNullOrWhiteSpace(contact.Callsign))
            {
                contacts.Add(contact);
                continue;
            }

            var match = FindBestHistoricalMatch(contact, history, pilots, usedPilotIndexes, ambiguousPilotIndexes) ??
                FindBestMatch(contact, pilots, usedPilotIndexes, ambiguousPilotIndexes, timestampedPilotIndexes);
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

    public static VatsimMatchDiagnostics InspectBestHistoricalMatch(
        TrafficContactState currentContact,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        VatsimMatchDiagnostics? best = null;
        for (var i = 0; i < pilots.Count; i++)
        {
            var pilot = pilots[i];
            if (pilot.LastUpdated is not DateTimeOffset lastUpdated)
            {
                continue;
            }

            var historicalContact = FindHistoricalContact(currentContact.Id, history, lastUpdated);
            if (historicalContact is null)
            {
                continue;
            }

            var candidate = InspectCandidate(historicalContact, pilot);
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
        IReadOnlySet<int> usedPilotIndexes,
        IReadOnlySet<int> ambiguousPilotIndexes,
        IReadOnlySet<int>? blockedPilotIndexes = null)
    {
        (int Index, VatsimPilotCandidate Pilot, double Score)? best = null;
        (int Index, VatsimPilotCandidate Pilot, double Score)? secondBest = null;
        for (var i = 0; i < pilots.Count; i++)
        {
            if (usedPilotIndexes.Contains(i) ||
                ambiguousPilotIndexes.Contains(i) ||
                blockedPilotIndexes?.Contains(i) == true)
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
                secondBest = best;
                best = (i, pilot, score);
                continue;
            }

            if (secondBest is null || score < secondBest.Value.Score)
            {
                secondBest = (i, pilot, score);
            }
        }

        if (best is null)
        {
            return null;
        }

        if (secondBest is not null && secondBest.Value.Score - best.Value.Score < MinBestScoreMargin)
        {
            return null;
        }

        return (best.Value.Index, best.Value.Pilot);
    }

    private static (int Index, VatsimPilotCandidate Pilot)? FindBestHistoricalMatch(
        TrafficContactState currentContact,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlySet<int> usedPilotIndexes,
        IReadOnlySet<int> ambiguousPilotIndexes)
    {
        (int Index, VatsimPilotCandidate Pilot, double Score)? best = null;
        (int Index, VatsimPilotCandidate Pilot, double Score)? secondBest = null;
        for (var i = 0; i < pilots.Count; i++)
        {
            var pilot = pilots[i];
            if (usedPilotIndexes.Contains(i) ||
                ambiguousPilotIndexes.Contains(i) ||
                pilot.LastUpdated is not DateTimeOffset lastUpdated)
            {
                continue;
            }

            var historicalContact = FindHistoricalContact(currentContact.Id, history, lastUpdated);
            if (historicalContact is null || !IsCandidate(historicalContact, pilot, out var score))
            {
                continue;
            }

            if (best is null || score < best.Value.Score)
            {
                secondBest = best;
                best = (i, pilot, score);
                continue;
            }

            if (secondBest is null || score < secondBest.Value.Score)
            {
                secondBest = (i, pilot, score);
            }
        }

        if (best is null)
        {
            return null;
        }

        if (secondBest is not null && secondBest.Value.Score - best.Value.Score < MinBestScoreMargin)
        {
            return null;
        }

        return (best.Value.Index, best.Value.Pilot);
    }

    private static HashSet<int> FindAmbiguousPilotIndexes(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var ambiguousPilotIndexes = new HashSet<int>();
        for (var i = 0; i < pilots.Count; i++)
        {
            if (HasAmbiguousContactCandidates(contacts, pilots[i]))
            {
                ambiguousPilotIndexes.Add(i);
            }
        }

        return ambiguousPilotIndexes;
    }

    private static HashSet<int> FindTimestampedPilotIndexes(IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var timestampedPilotIndexes = new HashSet<int>();
        for (var i = 0; i < pilots.Count; i++)
        {
            if (pilots[i].LastUpdated.HasValue)
            {
                timestampedPilotIndexes.Add(i);
            }
        }

        return timestampedPilotIndexes;
    }

    private static HashSet<int> FindAmbiguousHistoricalPilotIndexes(
        IReadOnlyList<TrafficContactState> currentContacts,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var ambiguousPilotIndexes = new HashSet<int>();
        for (var i = 0; i < pilots.Count; i++)
        {
            var pilot = pilots[i];
            if (pilot.LastUpdated is not DateTimeOffset lastUpdated)
            {
                continue;
            }

            var historicalContacts = currentContacts
                .Select(contact => FindHistoricalContact(contact.Id, history, lastUpdated))
                .Where(static contact => contact is not null)
                .Cast<TrafficContactState>()
                .ToList();

            if (HasAmbiguousContactCandidates(historicalContacts, pilot))
            {
                ambiguousPilotIndexes.Add(i);
            }
        }

        return ambiguousPilotIndexes;
    }

    private static bool HasAmbiguousContactCandidates(
        IReadOnlyList<TrafficContactState> contacts,
        VatsimPilotCandidate pilot)
    {
        double? bestScore = null;
        double? secondBestScore = null;
        foreach (var contact in contacts)
        {
            if (!IsCandidate(contact, pilot, out var score))
            {
                continue;
            }

            if (bestScore is null || score < bestScore.Value)
            {
                secondBestScore = bestScore;
                bestScore = score;
                continue;
            }

            if (secondBestScore is null || score < secondBestScore.Value)
            {
                secondBestScore = score;
            }
        }

        return bestScore is not null &&
            secondBestScore is not null &&
            secondBestScore.Value - bestScore.Value < MinPilotBestScoreMargin;
    }

    private static TrafficContactState? FindHistoricalContact(
        string contactId,
        IReadOnlyList<TrafficSnapshot> history,
        DateTimeOffset targetTime)
    {
        TrafficContactState? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var snapshot in history)
        {
            var contact = snapshot.Contacts.FirstOrDefault(item => string.Equals(item.Id, contactId, StringComparison.OrdinalIgnoreCase));
            if (contact is null)
            {
                continue;
            }

            var delta = (contact.Timestamp - targetTime).Duration();
            if (delta < bestDelta)
            {
                best = contact;
                bestDelta = delta;
            }
        }

        return bestDelta <= MaxHistoricalMatchAge ? best : null;
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

        if (ShouldCheckMotion(contact, pilot))
        {
            if (headingPenalty > MaxMatchHeadingDeltaDeg)
            {
                return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "heading");
            }

            if (speedPenalty > MaxMatchSpeedDeltaKt)
            {
                return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "speed");
            }
        }

        return new VatsimMatchDiagnostics(true, pilot.Callsign, distanceNm, altitudeDeltaFt, score, null);
    }

    private static bool ShouldCheckMotion(TrafficContactState contact, VatsimPilotCandidate pilot) =>
        contact.HeadingDeg.HasValue &&
        contact.SpeedKt.HasValue &&
        contact.SpeedKt.Value >= MinAirborneSpeedForMotionCheckKt &&
        pilot.GroundspeedKt >= MinAirborneSpeedForMotionCheckKt;
}

public sealed record VatsimPilotCandidate(
    string Callsign,
    double LatitudeDeg,
    double LongitudeDeg,
    int AltitudeFt,
    int GroundspeedKt,
    int HeadingDeg,
    DateTimeOffset? LastUpdated = null);

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
