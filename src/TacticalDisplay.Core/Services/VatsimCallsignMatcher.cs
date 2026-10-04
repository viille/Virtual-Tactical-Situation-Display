using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public static class VatsimCallsignMatcher
{
    private const double MaxMatchDistanceNm = 1.5;
    private const double MaxMatchAltitudeFt = 800;
    private const double MaxMatchHeadingDeltaDeg = 45;
    private const double MaxMatchSpeedDeltaKt = 100;
    // MSFS multiplayer traffic can be several miles away from the VATSIM
    // feed position even when its motion is an excellent match. This is a
    // deliberately conservative second-pass gate, never used by the
    // position-only matcher.
    private const double FallbackMaxDistanceNm = 6.0;
    private const double FallbackMaxAltitudeDeltaFt = 800;
    private const double FallbackMaxHeadingDeltaDeg = 25;
    private const double FallbackMaxSpeedDeltaKt = 80;
    // A short-range contact is much safer to identify than a distant one.
    // MSFS sometimes omits one or both motion values for multiplayer objects;
    // allow that only in this tight envelope and still require uniqueness and
    // repeated confirmation in the app layer.
    private const double NearFallbackMaxDistanceNm = 2.0;
    private const double NearFallbackMaxAltitudeDeltaFt = 500;
    private const double MinFallbackScoreMargin = 1.5;
    private const double MinAirborneSpeedForMotionCheckKt = 40;
    private const double MinBestScoreMargin = 0.75;
    private const double MinGlobalAssignmentMargin = 0.25;
    private const double DirectMatchUnmatchedPenalty = 1.5;
    private const double FallbackMatchUnmatchedPenalty = 7.5;
    // Keep the historical match bounded so stale positions cannot be assigned
    // to a current VATSIM pilot. Interpolation handles the normal update gap.
    private static readonly TimeSpan MaxHistoricalMatchAge = TimeSpan.FromSeconds(20);

    /// <summary>Measures the existing safe assignment search without exposing internal candidate types.</summary>
    public static VatsimAssignmentSearchMetrics MeasureAssignmentSearch(
        int contactCount,
        int pilotCount,
        Func<int, int, double?> candidateScore)
    {
        ArgumentNullException.ThrowIfNull(candidateScore);
        var candidates = new List<MatchCandidate>();
        for (var contactIndex = 0; contactIndex < contactCount; contactIndex++)
        {
            for (var pilotIndex = 0; pilotIndex < pilotCount; pilotIndex++)
            {
                if (candidateScore(contactIndex, pilotIndex) is double score)
                {
                    candidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = FindBestAssignment(candidates, out var relaxedColumns);
        timer.Stop();
        var (componentContactCount, componentPilotCount) = GetLargestComponentSize(candidates);
        return new VatsimAssignmentSearchMetrics(
            candidates.Count,
            componentContactCount,
            componentPilotCount,
            timer.Elapsed,
            result.Select(candidate => (candidate.ContactIndex, candidate.PilotIndex)).ToArray(),
            relaxedColumns);
    }

    public static TrafficSnapshot EnrichSnapshot(
        TrafficSnapshot snapshot,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        VatsimOwnshipIdentity? ownshipIdentity = null)
    {
        pilots = ExcludeOwnshipPilots(pilots, ownshipIdentity);
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
        IReadOnlyList<VatsimPilotCandidate> pilots,
        VatsimOwnshipIdentity? ownshipIdentity = null)
    {
        pilots = ExcludeOwnshipPilots(pilots, ownshipIdentity);
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

                var historicalContact = FindHistoricalContact(contact, history, lastUpdated);
                if (historicalContact is not null && IsCandidate(historicalContact, pilot, out var score))
                {
                    candidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        var assignments = BuildAssignedCallsigns(contacts, pilots, candidates);
        var assignedPilotIndexes = assignments.Values
            .Select(callsign => FindPilotIndexByCallsign(pilots, callsign))
            .Where(static index => index >= 0)
            .ToHashSet();
        var fallbackCandidates = new List<MatchCandidate>();
        for (var contactIndex = 0; contactIndex < contacts.Count; contactIndex++)
        {
            if (!string.IsNullOrWhiteSpace(contacts[contactIndex].Callsign) ||
                assignments.ContainsKey(contacts[contactIndex].Id))
            {
                continue;
            }

            for (var pilotIndex = 0; pilotIndex < pilots.Count; pilotIndex++)
            {
                if (assignedPilotIndexes.Contains(pilotIndex) ||
                    pilots[pilotIndex].LastUpdated is not DateTimeOffset lastUpdated)
                {
                    continue;
                }

                var historicalContact = FindHistoricalContact(contacts[contactIndex], history, lastUpdated);
                if (historicalContact is not null &&
                    IsFallbackCandidate(historicalContact, pilots[pilotIndex], out var score))
                {
                    fallbackCandidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        foreach (var pair in BuildAssignedCallsigns(contacts, pilots, fallbackCandidates, MinFallbackScoreMargin, FallbackMatchUnmatchedPenalty))
        {
            assignments[pair.Key] = pair.Value;
        }

        return assignments;
    }

    private static Dictionary<string, string> BuildAssignedCallsigns(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots,
        IReadOnlyList<MatchCandidate> candidates,
        double minScoreMargin = MinBestScoreMargin,
        double unmatchedPenalty = DirectMatchUnmatchedPenalty)
    {
        var best = FindBestAssignment(candidates, minScoreMargin, unmatchedPenalty);
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

    /// <summary>Returns compact current-position assignment decisions for diagnostics.</summary>
    public static IReadOnlyDictionary<string, VatsimAssignmentDecision> InspectCurrentAssignment(
        IReadOnlyList<TrafficContactState> contacts,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var candidates = new List<MatchCandidate>();
        for (var contactIndex = 0; contactIndex < contacts.Count; contactIndex++)
        {
            if (!string.IsNullOrWhiteSpace(contacts[contactIndex].Callsign)) continue;
            for (var pilotIndex = 0; pilotIndex < pilots.Count; pilotIndex++)
            {
                if (IsCandidate(contacts[contactIndex], pilots[pilotIndex], out var score))
                {
                    candidates.Add(new MatchCandidate(contactIndex, pilotIndex, score));
                }
            }
        }

        var stablePairs = FindBestAssignment(candidates).ToDictionary(pair => pair.ContactIndex);
        var candidatesByContact = candidates.GroupBy(pair => pair.ContactIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var candidatesByPilot = candidates.GroupBy(pair => pair.PilotIndex)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.ContactIndex).Distinct().ToArray());
        var componentByContact = new Dictionary<int, int>();
        var componentSizeByContact = new Dictionary<int, int>();
        foreach (var root in candidatesByContact.Keys.Order())
        {
            if (componentByContact.ContainsKey(root)) continue;
            var componentContacts = new HashSet<int>();
            var componentPilots = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var contactIndex))
            {
                if (!componentContacts.Add(contactIndex)) continue;
                foreach (var edge in candidatesByContact[contactIndex])
                {
                    if (!componentPilots.Add(edge.PilotIndex)) continue;
                    foreach (var neighbor in candidatesByPilot[edge.PilotIndex]) queue.Enqueue(neighbor);
                }
            }

            var componentId = componentContacts.Min();
            foreach (var contactIndex in componentContacts)
            {
                componentByContact[contactIndex] = componentId;
                componentSizeByContact[contactIndex] = componentContacts.Count;
            }
        }

        var decisions = new Dictionary<string, VatsimAssignmentDecision>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < contacts.Count; index++)
        {
            candidatesByContact.TryGetValue(index, out var options);
            var hasStablePair = stablePairs.TryGetValue(index, out var pair);
            var bestScore = options?.Min(candidate => candidate.Score);
            var unmatchedAlternativeCost = bestScore.HasValue ? DirectMatchUnmatchedPenalty - bestScore.Value : 0;
            var reason = hasStablePair ? "stable-across-plausible-solutions" : options is null ? "no-valid-candidate" :
                unmatchedAlternativeCost < MinBestScoreMargin ? "unmatched-is-plausible" :
                options.Length > 1 ? "multiple-pilot-alternatives" : "global-assignment-conflict";
            decisions[contacts[index].Id] = new VatsimAssignmentDecision(
                hasStablePair && pair is not null ? pilots[pair.PilotIndex].Callsign.Trim().ToUpperInvariant() : null,
                componentByContact.GetValueOrDefault(index, -1),
                componentSizeByContact.GetValueOrDefault(index),
                hasStablePair,
                unmatchedAlternativeCost,
                reason);
        }

        return decisions;
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

            var historicalContact = FindHistoricalContact(currentContact, history, lastUpdated);
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
        IReadOnlyList<MatchCandidate> candidates,
        double minScoreMargin = MinBestScoreMargin,
        double unmatchedPenalty = DirectMatchUnmatchedPenalty) =>
        FindBestAssignment(candidates, out _, minScoreMargin, unmatchedPenalty);

    private static IReadOnlyList<MatchCandidate> FindBestAssignment(
        IReadOnlyList<MatchCandidate> candidates,
        out long relaxedColumns,
        double minScoreMargin = MinBestScoreMargin,
        double unmatchedPenalty = DirectMatchUnmatchedPenalty)
    {
        relaxedColumns = 0;
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidatesByContact = candidates
            .GroupBy(static candidate => candidate.ContactIndex)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(candidate => candidate.Score).ToArray());
        var candidatesByPilot = candidates
            .GroupBy(static candidate => candidate.PilotIndex)
            .ToDictionary(static group => group.Key, static group => group.Select(candidate => candidate.ContactIndex).Distinct().ToArray());
        var unvisitedContacts = candidatesByContact.Keys.ToHashSet();
        var stableAssignments = new List<MatchCandidate>();
        var plausibleSolutionMargin = System.Math.Max(minScoreMargin, MinGlobalAssignmentMargin);

        while (unvisitedContacts.Count > 0)
        {
            var componentContacts = new HashSet<int>();
            var componentPilots = new HashSet<int>();
            var pendingContacts = new Queue<int>();
            pendingContacts.Enqueue(unvisitedContacts.First());
            while (pendingContacts.TryDequeue(out var contactIndex))
            {
                if (!componentContacts.Add(contactIndex)) continue;
                unvisitedContacts.Remove(contactIndex);
                foreach (var candidate in candidatesByContact[contactIndex])
                {
                    if (!componentPilots.Add(candidate.PilotIndex)) continue;
                    foreach (var neighboringContact in candidatesByPilot[candidate.PilotIndex])
                    {
                        if (!componentContacts.Contains(neighboringContact)) pendingContacts.Enqueue(neighboringContact);
                    }
                }
            }

            var componentCandidates = componentContacts
                .Order()
                .Select(contactIndex => candidatesByContact[contactIndex])
                .ToArray();
            var bestScore = FindMinimum(componentCandidates, unmatchedPenalty, -1, -1, ref relaxedColumns);
            foreach (var options in componentCandidates)
            {
                var contactIndex = options[0].ContactIndex;
                var plausibleCandidates = new List<MatchCandidate>();
                foreach (var candidate in options)
                {
                    if (FindMinimum(componentCandidates, unmatchedPenalty, contactIndex, candidate.PilotIndex, ref relaxedColumns)
                        <= bestScore + plausibleSolutionMargin)
                    {
                        plausibleCandidates.Add(candidate);
                    }
                }
                var unmatchedIsPlausible = FindMinimum(componentCandidates, unmatchedPenalty, contactIndex, -1, ref relaxedColumns)
                    <= bestScore + plausibleSolutionMargin;
                if (!unmatchedIsPlausible && plausibleCandidates.Count == 1)
                {
                    stableAssignments.Add(plausibleCandidates[0]);
                }
            }
        }

        return stableAssignments;
    }

    private static double FindMinimum(
        IReadOnlyList<MatchCandidate[]> componentCandidates,
        double unmatchedPenalty,
        int forcedContactIndex,
        int forcedPilotIndex,
        ref long relaxedColumns)
    {
        var forcedCost = 0.0;
        var rows = new List<MatchCandidate[]>();
        foreach (var options in componentCandidates)
        {
            if (options[0].ContactIndex != forcedContactIndex)
            {
                rows.Add(options);
                continue;
            }

            if (forcedPilotIndex >= 0)
            {
                var forced = options.FirstOrDefault(candidate => candidate.PilotIndex == forcedPilotIndex);
                if (forced is null) return double.PositiveInfinity;
                forcedCost = forced.Score - unmatchedPenalty;
            }
        }

        if (rows.Count == 0) return forcedCost;
        var pilotIndexes = componentCandidates.SelectMany(options => options)
            .Select(candidate => candidate.PilotIndex)
            .Distinct()
            .Where(pilotIndex => pilotIndex != forcedPilotIndex)
            .Order()
            .ToArray();
        var rowCount = rows.Count;
        var columnCount = pilotIndexes.Length + rowCount;
        var costs = new double[rowCount, columnCount];
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < pilotIndexes.Length; column++)
            {
                var candidate = rows[row].FirstOrDefault(option => option.PilotIndex == pilotIndexes[column]);
                costs[row, column] = candidate is null ? 1e9 : candidate.Score - unmatchedPenalty;
            }
        }

        // Rectangular Hungarian assignment. One zero-cost dummy column per contact
        // represents the explicit unmatched alternative; candidate edges retain
        // their original score-minus-unmatched-penalty objective.
        var u = new double[rowCount + 1];
        var v = new double[columnCount + 1];
        var matching = new int[columnCount + 1];
        var previousColumn = new int[columnCount + 1];
        for (var row = 1; row <= rowCount; row++)
        {
            matching[0] = row;
            var currentColumn = 0;
            var minimumReducedCost = Enumerable.Repeat(double.PositiveInfinity, columnCount + 1).ToArray();
            var usedColumns = new bool[columnCount + 1];
            do
            {
                usedColumns[currentColumn] = true;
                var currentRow = matching[currentColumn];
                var delta = double.PositiveInfinity;
                var nextColumn = 0;
                for (var column = 1; column <= columnCount; column++)
                {
                    if (usedColumns[column]) continue;
                    relaxedColumns++;
                    var cost = column > pilotIndexes.Length ? 0 : costs[currentRow - 1, column - 1];
                    var reducedCost = cost - u[currentRow] - v[column];
                    if (reducedCost < minimumReducedCost[column])
                    {
                        minimumReducedCost[column] = reducedCost;
                        previousColumn[column] = currentColumn;
                    }

                    if (minimumReducedCost[column] < delta)
                    {
                        delta = minimumReducedCost[column];
                        nextColumn = column;
                    }
                }

                for (var column = 0; column <= columnCount; column++)
                {
                    if (usedColumns[column])
                    {
                        u[matching[column]] += delta;
                        v[column] -= delta;
                    }
                    else
                    {
                        minimumReducedCost[column] -= delta;
                    }
                }

                currentColumn = nextColumn;
            }
            while (matching[currentColumn] != 0);

            do
            {
                var nextColumn = previousColumn[currentColumn];
                matching[currentColumn] = matching[nextColumn];
                currentColumn = nextColumn;
            }
            while (currentColumn != 0);
        }

        return forcedCost - v[0];
    }

    private static (int Contacts, int Pilots) GetLargestComponentSize(IReadOnlyList<MatchCandidate> candidates)
    {
        var contactsByPilot = candidates.GroupBy(candidate => candidate.PilotIndex)
            .ToDictionary(group => group.Key, group => group.Select(candidate => candidate.ContactIndex).Distinct().ToArray());
        var pilotsByContact = candidates.GroupBy(candidate => candidate.ContactIndex)
            .ToDictionary(group => group.Key, group => group.Select(candidate => candidate.PilotIndex).Distinct().ToArray());
        var visitedContacts = new HashSet<int>();
        var largestContacts = 0;
        var largestPilots = 0;
        foreach (var root in pilotsByContact.Keys)
        {
            if (visitedContacts.Contains(root)) continue;
            var contacts = new HashSet<int>();
            var pilots = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var contact))
            {
                if (!contacts.Add(contact)) continue;
                visitedContacts.Add(contact);
                foreach (var pilot in pilotsByContact[contact])
                {
                    if (!pilots.Add(pilot)) continue;
                    foreach (var neighbor in contactsByPilot[pilot]) queue.Enqueue(neighbor);
                }
            }

            if (contacts.Count > largestContacts)
            {
                largestContacts = contacts.Count;
                largestPilots = pilots.Count;
            }
        }

        return (largestContacts, largestPilots);
    }

    private static IReadOnlyList<VatsimPilotCandidate> ExcludeOwnshipPilots(
        IReadOnlyList<VatsimPilotCandidate> pilots,
        VatsimOwnshipIdentity? ownshipIdentity) =>
        ownshipIdentity is null ? pilots : pilots.Where(pilot =>
            !(!string.IsNullOrWhiteSpace(ownshipIdentity.Cid)
                ? string.Equals(pilot.Cid, ownshipIdentity.Cid, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(ownshipIdentity.Callsign) && string.Equals(pilot.Callsign, ownshipIdentity.Callsign, StringComparison.OrdinalIgnoreCase))).ToArray();

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
        TrafficContactState currentContact,
        IReadOnlyList<TrafficSnapshot> history,
        DateTimeOffset targetTime)
    {
        var samples = new List<TrafficContactState>();
        foreach (var snapshot in history)
        {
            var contact = snapshot.Contacts.FirstOrDefault(item =>
                string.Equals(item.Id, currentContact.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Generation == currentContact.Generation);
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
        var distance = GeoMath.DistanceNm(before.LatitudeDeg, before.LongitudeDeg, after.LatitudeDeg, after.LongitudeDeg);
        var track = GeoMath.InitialBearingDeg(before.LatitudeDeg, before.LongitudeDeg, after.LatitudeDeg, after.LongitudeDeg);
        var position = GeoMath.DestinationPoint(before.LatitudeDeg, before.LongitudeDeg, track, distance * fraction);
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
            position.latitudeDeg,
            position.longitudeDeg,
            before.AltitudeFt + ((after.AltitudeFt - before.AltitudeFt) * fraction),
            heading,
            speed,
            timestamp,
            Generation: before.Generation,
            GroundTrackDeg: before.GroundTrackDeg.HasValue && after.GroundTrackDeg.HasValue
                ? GeoMath.NormalizeDegrees(before.GroundTrackDeg.Value + GeoMath.SignedRelativeBearingDeg(before.GroundTrackDeg.Value, after.GroundTrackDeg.Value) * fraction)
                : null);
    }

    private static bool IsCandidate(TrafficContactState contact, VatsimPilotCandidate pilot, out double score)
    {
        var diagnostics = InspectStrictCandidate(contact, pilot);
        score = diagnostics.Score;
        return diagnostics.IsMatch;
    }

    private static VatsimMatchDiagnostics InspectCandidate(TrafficContactState contact, VatsimPilotCandidate pilot)
    {
        var strict = InspectStrictCandidate(contact, pilot);
        if (strict.IsMatch)
        {
            return strict;
        }

        var fallback = CreateFallbackDiagnostics(contact, pilot);
        return fallback.IsMatch ? fallback : strict;
    }

    private static VatsimMatchDiagnostics InspectStrictCandidate(TrafficContactState contact, VatsimPilotCandidate pilot)
    {
        var distanceNm = GeoMath.DistanceNm(contact.LatitudeDeg, contact.LongitudeDeg, pilot.LatitudeDeg, pilot.LongitudeDeg);
        var altitudeDeltaFt = System.Math.Abs(contact.AltitudeFt - pilot.AltitudeFt);
        var headingPenalty = contact.HeadingDeg.HasValue
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(contact.HeadingDeg.Value, pilot.HeadingDeg))
            : 0;
        var speedPenalty = contact.SpeedKt.HasValue
            ? System.Math.Abs(contact.SpeedKt.Value - pilot.GroundspeedKt)
            : 0;
        var reliableMotion = ShouldCheckMotion(contact, pilot);
        var score = distanceNm + altitudeDeltaFt / 1000.0 +
            (reliableMotion ? headingPenalty / 180.0 + speedPenalty / 360.0 : 0);

        if (distanceNm > MaxMatchDistanceNm)
        {
            return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "distance");
        }

        if (altitudeDeltaFt > MaxMatchAltitudeFt)
        {
            return new VatsimMatchDiagnostics(false, pilot.Callsign, distanceNm, altitudeDeltaFt, score, "altitude");
        }

        if (reliableMotion)
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

    private static bool IsFallbackCandidate(
        TrafficContactState contact,
        VatsimPilotCandidate pilot,
        out double score)
    {
        var diagnostics = CreateFallbackDiagnostics(contact, pilot);
        score = diagnostics.Score;
        return diagnostics.IsMatch;
    }

    private static VatsimMatchDiagnostics CreateFallbackDiagnostics(
        TrafficContactState contact,
        VatsimPilotCandidate pilot)
    {
        var distanceNm = GeoMath.DistanceNm(contact.LatitudeDeg, contact.LongitudeDeg, pilot.LatitudeDeg, pilot.LongitudeDeg);
        var altitudeDeltaFt = System.Math.Abs(contact.AltitudeFt - pilot.AltitudeFt);
        var hasHeading = contact.HeadingDeg.HasValue;
        var hasSpeed = contact.SpeedKt.HasValue;
        var headingDeltaDeg = hasHeading
            ? System.Math.Abs(GeoMath.SignedRelativeBearingDeg(contact.HeadingDeg!.Value, pilot.HeadingDeg))
            : 0;
        var speedDeltaKt = hasSpeed
            ? System.Math.Abs(contact.SpeedKt!.Value - pilot.GroundspeedKt)
            : 0;
        var hasReliableMotion = ShouldCheckMotion(contact, pilot);
        var score = distanceNm + altitudeDeltaFt / 1000.0 +
            (hasReliableMotion ? headingDeltaDeg / 90.0 + speedDeltaKt / 180.0 : 0);
        var isNearPositionMatch = distanceNm <= NearFallbackMaxDistanceNm &&
            altitudeDeltaFt <= NearFallbackMaxAltitudeDeltaFt &&
            (!hasReliableMotion ||
                ((!hasHeading || headingDeltaDeg <= MaxMatchHeadingDeltaDeg) &&
                 (!hasSpeed || speedDeltaKt <= MaxMatchSpeedDeltaKt)));
        var isMatch = (hasReliableMotion &&
            distanceNm <= FallbackMaxDistanceNm &&
            altitudeDeltaFt <= FallbackMaxAltitudeDeltaFt &&
            headingDeltaDeg <= FallbackMaxHeadingDeltaDeg &&
            speedDeltaKt <= FallbackMaxSpeedDeltaKt) ||
            (!hasReliableMotion && isNearPositionMatch);
        return new VatsimMatchDiagnostics(
            isMatch,
            pilot.Callsign,
            distanceNm,
            altitudeDeltaFt,
            score,
            isMatch ? null : "fallback-kinematics",
            IsFallback: isMatch);
    }

    private static bool ShouldCheckMotion(TrafficContactState contact, VatsimPilotCandidate pilot) =>
        contact.HeadingDeg.HasValue &&
        contact.SpeedKt.HasValue &&
        contact.SpeedKt.Value >= MinAirborneSpeedForMotionCheckKt &&
        pilot.GroundspeedKt >= MinAirborneSpeedForMotionCheckKt;
}

public sealed record VatsimAssignmentDecision(
    string? Callsign,
    int ComponentId,
    int ComponentContactCount,
    bool StableAcrossPlausibleSolutions,
    double UnmatchedAlternativeCost,
    string AmbiguityReason);

public sealed record VatsimAssignmentSearchMetrics(
    int CandidateEdges,
    int LargestComponentContactCount,
    int LargestComponentPilotCount,
    TimeSpan Elapsed,
    IReadOnlyList<(int ContactIndex, int PilotIndex)> StablePairs,
    long RelaxedColumns);

public sealed record VatsimPilotCandidate(
    string Callsign,
    double LatitudeDeg,
    double LongitudeDeg,
    int AltitudeFt,
    int GroundspeedKt,
    int HeadingDeg,
    DateTimeOffset? LastUpdated = null,
    string? Cid = null);

public sealed record VatsimOwnshipIdentity(string? Cid = null, string? Callsign = null);

public sealed record VatsimMatchDiagnostics(
    bool IsMatch,
    string? Callsign,
    double DistanceNm,
    double AltitudeDeltaFt,
    double Score,
    string? RejectReason,
    bool IsFallback = false)
{
    public static VatsimMatchDiagnostics None { get; } = new(false, null, 0, 0, double.MaxValue, "no-pilots");
}
