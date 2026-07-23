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
    private const double FormationMaxDistanceNm = 3.0;
    private const double FormationMaxAltitudeDeltaFt = 1500;
    private const double FormationMaxHeadingDeltaDeg = 60;
    private const double FormationMaxSpeedDeltaKt = 150;
    private const double SameCallsignGroupBonus = 0.35;
    // VATSIM updates are normally around 15 seconds apart. Allow one delayed
    // update, but never match against an arbitrarily old simulator position.
    private static readonly TimeSpan MaxHistoricalMatchAge = TimeSpan.FromSeconds(30);

    public static TrafficSnapshot EnrichSnapshot(
        TrafficSnapshot snapshot,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        if (pilots.Count == 0 || snapshot.Contacts.Count == 0)
        {
            return snapshot;
        }

        var assignedCallsigns = AssignCurrentMatches(snapshot.Contacts, pilots);
        return EnrichAssignedSnapshot(snapshot, assignedCallsigns);
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

        var assignedCallsigns = AssignHistoricalMatches(snapshot.Contacts, history, pilots);
        var unresolvedContacts = snapshot.Contacts
            .Where(contact =>
                string.IsNullOrWhiteSpace(contact.Callsign) &&
                !assignedCallsigns.ContainsKey(contact.Id))
            .ToList();
        var timestampedPilotIndexes = FindTimestampedPilotIndexes(pilots);
        var assignedPilotIndexes = assignedCallsigns.Values
            .Select(callsign => FindPilotIndexByCallsign(pilots, callsign))
            .Where(static index => index >= 0)
            .ToHashSet();
        foreach (var pair in AssignCurrentMatches(unresolvedContacts, pilots, timestampedPilotIndexes, assignedPilotIndexes))
        {
            assignedCallsigns[pair.Key] = pair.Value;
        }

        return EnrichAssignedSnapshot(snapshot, assignedCallsigns);
    }

    private static TrafficSnapshot EnrichAssignedSnapshot(
        TrafficSnapshot snapshot,
        IReadOnlyDictionary<string, string> assignedCallsigns)
    {
        var contacts = new List<TrafficContactState>(snapshot.Contacts.Count);
        foreach (var contact in snapshot.Contacts)
        {
            contacts.Add(string.IsNullOrWhiteSpace(contact.Callsign) &&
                assignedCallsigns.TryGetValue(contact.Id, out var callsign)
                    ? contact with { Callsign = callsign }
                    : contact);
        }

        return snapshot with { Contacts = contacts };
    }

    private static Dictionary<string, string> AssignCurrentMatches(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlySet<int>? blockedPilotIndexes = null,
        IReadOnlySet<int>? usedPilotIndexes = null)
    {
        var candidates = new List<MatchCandidate>();
        for (var contactIndex = 0; contactIndex < contacts.Count; contactIndex++)
        {
            var contact = contacts[contactIndex];
            if (!string.IsNullOrWhiteSpace(contact.Callsign))
            {
                continue;
            }

            for (var pilotIndex = 0; pilotIndex < pilots.Count; pilotIndex++)
            {
                if (blockedPilotIndexes?.Contains(pilotIndex) == true ||
                    usedPilotIndexes?.Contains(pilotIndex) == true)
                {
                    continue;
                }

                if (IsCandidate(contact, pilots[pilotIndex], out var score))
                {
                    candidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        return BuildAssignedCallsigns(contacts, pilots, candidates);
    }

    private static Dictionary<string, string> AssignHistoricalMatches(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var candidates = new List<MatchCandidate>();
        for (var contactIndex = 0; contactIndex < contacts.Count; contactIndex++)
        {
            var contact = contacts[contactIndex];
            if (!string.IsNullOrWhiteSpace(contact.Callsign))
            {
                continue;
            }

            for (var pilotIndex = 0; pilotIndex < pilots.Count; pilotIndex++)
            {
                var pilot = pilots[pilotIndex];
                if (pilot.LastUpdated is not DateTimeOffset lastUpdated)
                {
                    continue;
                }

                var historicalContact = FindHistoricalContact(contact.Id, history, lastUpdated);
                if (historicalContact is not null && IsCandidate(historicalContact, pilot, out var score))
                {
                    candidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        return BuildAssignedCallsigns(contacts, pilots, candidates);
    }

    private static Dictionary<string, string> BuildAssignedCallsigns(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlyList<MatchCandidate> candidates)
    {
        var best = FindBestAssignment(contacts, pilots, candidates);
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in best)
        {
            assignments[contacts[candidate.ContactIndex].Id] = pilots[candidate.PilotIndex].Callsign.Trim().ToUpperInvariant();
        }

        return assignments;
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

    public static VatsimMatchDiagnostics InspectMatch(
        TrafficContactState contact,
        VatsimPilotCandidate pilot) =>
        InspectCandidate(contact, pilot);

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

    private static IReadOnlyList<MatchCandidate> FindBestAssignment(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlyList<MatchCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidatesByContact = candidates
            .GroupBy(static candidate => candidate.ContactIndex)
            .OrderBy(static group => group.Key)
            .Select(static group => group.OrderBy(static candidate => candidate.Score).ToList())
            .ToList();
        if (candidatesByContact.Count == 1 &&
            candidatesByContact[0].Count > 1 &&
            candidatesByContact[0][1].Score - candidatesByContact[0][0].Score < MinBestScoreMargin)
        {
            return [];
        }

        var current = new List<MatchCandidate>();
        var usedPilots = new HashSet<int>();
        List<MatchCandidate> best = [];
        var bestScore = double.MaxValue;

        Search(0, 0);
        return best;

        void Search(int groupIndex, double score)
        {
            if (groupIndex >= candidatesByContact.Count)
            {
                if (current.Count > best.Count ||
                    (current.Count == best.Count && score < bestScore))
                {
                    best = [.. current];
                    bestScore = score;
                }

                return;
            }

            var remaining = candidatesByContact.Count - groupIndex;
            if (current.Count + remaining < best.Count)
            {
                return;
            }

            foreach (var candidate in candidatesByContact[groupIndex])
            {
                if (!usedPilots.Add(candidate.PilotIndex))
                {
                    continue;
                }

                current.Add(candidate);
                Search(groupIndex + 1, score + candidate.Score - GetFormationGroupBonus(candidate, current, contacts, pilots));
                current.RemoveAt(current.Count - 1);
                usedPilots.Remove(candidate.PilotIndex);
            }

            Search(groupIndex + 1, score);
        }
    }

    private static double GetFormationGroupBonus(
        MatchCandidate candidate,
        IReadOnlyList<MatchCandidate> assigned,
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var candidateContact = contacts[candidate.ContactIndex];
        var candidatePilot = pilots[candidate.PilotIndex];
        var candidateGroup = GetCallsignGroup(candidatePilot.Callsign);
        if (candidateGroup is null)
        {
            return 0;
        }

        foreach (var existing in assigned)
        {
            if (existing.ContactIndex == candidate.ContactIndex)
            {
                continue;
            }

            var existingPilot = pilots[existing.PilotIndex];
            if (!string.Equals(candidateGroup, GetCallsignGroup(existingPilot.Callsign), StringComparison.OrdinalIgnoreCase) ||
                !AreFormationNeighbors(candidateContact, contacts[existing.ContactIndex]))
            {
                continue;
            }

            return SameCallsignGroupBonus;
        }

        return 0;
    }

    private static bool AreFormationNeighbors(
        TrafficContactState first,
        TrafficContactState second)
    {
        if (GeoMath.DistanceNm(
                first.LatitudeDeg,
                first.LongitudeDeg,
                second.LatitudeDeg,
                second.LongitudeDeg) > FormationMaxDistanceNm ||
            System.Math.Abs(first.AltitudeFt - second.AltitudeFt) > FormationMaxAltitudeDeltaFt)
        {
            return false;
        }

        if (first.HeadingDeg.HasValue && second.HeadingDeg.HasValue &&
            System.Math.Abs(GeoMath.SignedRelativeBearingDeg(first.HeadingDeg.Value, second.HeadingDeg.Value)) > FormationMaxHeadingDeltaDeg)
        {
            return false;
        }

        return !first.SpeedKt.HasValue || !second.SpeedKt.HasValue ||
            System.Math.Abs(first.SpeedKt.Value - second.SpeedKt.Value) <= FormationMaxSpeedDeltaKt;
    }

    private static string? GetCallsignGroup(string callsign)
    {
        var normalized = callsign.Trim().ToUpperInvariant();
        var suffixStart = normalized.Length;
        while (suffixStart > 0 && char.IsDigit(normalized[suffixStart - 1]))
        {
            suffixStart--;
        }

        return suffixStart == 0 || suffixStart == normalized.Length
            ? null
            : normalized[..suffixStart];
    }

    private static int FindPilotIndexByCallsign(IReadOnlyList<VatsimPilotCandidate> pilots, string callsign)
    {
        for (var i = 0; i < pilots.Count; i++)
        {
            if (string.Equals(pilots[i].Callsign, callsign, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record MatchCandidate(int ContactIndex, int PilotIndex, double Score);

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

    private static TrafficContactState? FindHistoricalContact(
        string contactId,
        IReadOnlyList<TrafficSnapshot> history,
        DateTimeOffset targetTime)
    {
        var samples = new List<TrafficContactState>();
        foreach (var snapshot in history)
        {
            var contact = snapshot.Contacts.FirstOrDefault(item => string.Equals(item.Id, contactId, StringComparison.OrdinalIgnoreCase));
            if (contact is not null)
            {
                samples.Add(contact);
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var ordered = samples
            .OrderBy(static sample => sample.Timestamp)
            .ToList();
        var exact = ordered.FirstOrDefault(sample => sample.Timestamp == targetTime);
        if (exact is not null)
        {
            return exact;
        }

        TrafficContactState? before = null;
        TrafficContactState? after = null;
        foreach (var sample in ordered)
        {
            if (sample.Timestamp < targetTime)
            {
                before = sample;
                continue;
            }

            after = sample;
            break;
        }

        // Interpolating between the two surrounding observations is more
        // accurate than selecting a nearby sample for fast-moving traffic.
        if (before is not null && after is not null)
        {
            var beforeAge = targetTime - before.Timestamp;
            var afterAge = after.Timestamp - targetTime;
            if (beforeAge <= MaxHistoricalMatchAge && afterAge <= MaxHistoricalMatchAge)
            {
                var span = (after.Timestamp - before.Timestamp).TotalSeconds;
                if (span > 0)
                {
                    return Interpolate(before, after, (targetTime - before.Timestamp).TotalSeconds / span, targetTime);
                }
            }
        }

        var nearest = ordered
            .OrderBy(sample => (sample.Timestamp - targetTime).Duration())
            .First();
        return (nearest.Timestamp - targetTime).Duration() <= MaxHistoricalMatchAge
            ? nearest
            : null;
    }

    private static TrafficContactState Interpolate(
        TrafficContactState before,
        TrafficContactState after,
        double fraction,
        DateTimeOffset timestamp)
    {
        fraction = System.Math.Clamp(fraction, 0, 1);
        double? heading = before.HeadingDeg.HasValue && after.HeadingDeg.HasValue
            ? GeoMath.NormalizeDegrees(before.HeadingDeg.Value +
                (GeoMath.SignedRelativeBearingDeg(before.HeadingDeg.Value, after.HeadingDeg.Value) * fraction))
            : null;
        double? speed = before.SpeedKt.HasValue && after.SpeedKt.HasValue
            ? before.SpeedKt.Value + ((after.SpeedKt.Value - before.SpeedKt.Value) * fraction)
            : null;

        return new TrafficContactState(
            before.Id,
            before.Callsign ?? after.Callsign,
            before.LatitudeDeg + ((after.LatitudeDeg - before.LatitudeDeg) * fraction),
            before.LongitudeDeg + ((after.LongitudeDeg - before.LongitudeDeg) * fraction),
            before.AltitudeFt + ((after.AltitudeFt - before.AltitudeFt) * fraction),
            heading,
            speed,
            timestamp);
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
