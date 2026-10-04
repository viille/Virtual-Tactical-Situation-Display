using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TacticalDisplay.Tests;

public sealed class VatsimCallsignMatcherTests(ITestOutputHelper output)
{
    [Fact]
    public void AssignmentSearch_LargeSymmetricFormationStaysAmbiguousWithinWorkAndTimeLimits()
    {
        var report = new StringBuilder("| Contacts | Pilots | Candidate edges | Largest component | Relaxed columns | Search ms | Stable pairs |\n|---:|---:|---:|---:|---:|---:|---:|\n");
        foreach (var size in new[] { 6, 8, 10, 12, 15 })
        {
            var metrics = VatsimCallsignMatcher.MeasureAssignmentSearch(size, size, (_, _) => 1.0);

            Assert.Equal(size * size, metrics.CandidateEdges);
            Assert.Equal(size, metrics.LargestComponentContactCount);
            Assert.Equal(size, metrics.LargestComponentPilotCount);
            Assert.True(metrics.RelaxedColumns < 2_000_000,
                $"{size}x{size} symmetric assignment relaxed {metrics.RelaxedColumns} columns.");
            Assert.Empty(metrics.StablePairs);
            var searchMilliseconds = metrics.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var measurement = $"callsign-formation | contacts={size} pilots={size} edges={metrics.CandidateEdges} component={metrics.LargestComponentContactCount}x{metrics.LargestComponentPilotCount} relaxedColumns={metrics.RelaxedColumns} searchMs={searchMilliseconds} stablePairs={metrics.StablePairs.Count}";
            output.WriteLine(measurement);
            Console.WriteLine(measurement);
            report.AppendLine($"| {size} | {size} | {metrics.CandidateEdges} | {metrics.LargestComponentContactCount}x{metrics.LargestComponentPilotCount} | {metrics.RelaxedColumns} | {searchMilliseconds} | {metrics.StablePairs.Count} |");
            Assert.True(metrics.Elapsed < TimeSpan.FromSeconds(2),
                $"{size}x{size} symmetric assignment search took {metrics.Elapsed.TotalSeconds:0.000}s.");

            var now = DateTimeOffset.UnixEpoch;
            var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 0, now),
                Enumerable.Range(0, size).Select(index => new TrafficContactState($"T{index}", null, 60.1, 24, 5000, 90, 250, now)).ToArray(), now);
            var pilots = Enumerable.Range(0, size).Select(index => new VatsimPilotCandidate($"FIN{index:000}", 60.1, 24, 5000, 250, 90)).ToArray();
            var enriched = VatsimCallsignMatcher.EnrichSnapshot(snapshot, pilots);
            Assert.All(enriched.Contacts, contact => Assert.Null(contact.Callsign));
            var publishedCallsigns = enriched.Contacts.Where(contact => contact.Callsign is not null).Select(contact => contact.Callsign!).ToArray();
            Assert.Equal(publishedCallsigns.Length, publishedCallsigns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(summaryPath))
        {
            File.AppendAllText(summaryPath, "## Callsign assignment benchmark\n\n" + report + "\n", Encoding.UTF8);
        }
    }

    [Fact]
    public void AssignmentSearch_KeepsUnambiguousPairBesideAmbiguousFormation()
    {
        var metrics = VatsimCallsignMatcher.MeasureAssignmentSearch(4, 4, (contact, pilot) =>
            contact == 3 && pilot == 3 ? 0.1 : contact < 3 && pilot < 3 ? 1.0 : null);

        Assert.Equal(10, metrics.CandidateEdges);
        Assert.Contains((3, 3), metrics.StablePairs);
        Assert.DoesNotContain(metrics.StablePairs, pair => pair.ContactIndex < 3);
        Assert.Equal(metrics.StablePairs.Count, metrics.StablePairs.Select(pair => pair.PilotIndex).Distinct().Count());
    }

    [Fact]
    public void AssignmentSearch_MatchesExhaustivePlausibleIntersectionForSmallCandidateGraphs()
    {
        var random = new Random(73015);
        double[] scores = [0.1, 0.35, 0.6, 1.0, 1.4, 1.6, 1.9, 2.2];
        for (var size = 2; size <= 5; size++)
        {
            for (var example = 0; example < 20; example++)
            {
                var matrix = new double?[size, size];
                for (var contact = 0; contact < size; contact++)
                {
                    for (var pilot = 0; pilot < size; pilot++)
                    {
                        if (contact == 0 || pilot == 0 || random.NextDouble() < 0.65)
                        {
                            matrix[contact, pilot] = scores[random.Next(scores.Length)];
                        }
                    }
                }

                var actual = VatsimCallsignMatcher.MeasureAssignmentSearch(size, size,
                    (contact, pilot) => matrix[contact, pilot]).StablePairs.ToHashSet();
                var expected = ExhaustiveStablePairs(matrix);
                Assert.Equal(expected.OrderBy(pair => pair.ContactIndex).ThenBy(pair => pair.PilotIndex),
                    actual.OrderBy(pair => pair.ContactIndex).ThenBy(pair => pair.PilotIndex));
            }
        }
    }

    private static HashSet<(int ContactIndex, int PilotIndex)> ExhaustiveStablePairs(double?[,] scores)
    {
        var size = scores.GetLength(0);
        const double unmatchedPenalty = 1.5;
        const double plausibleMargin = 0.75;
        var usedPilots = new HashSet<int>();
        var current = new Dictionary<int, int>();
        var solutions = new List<(double Score, Dictionary<int, int> Assignments)>();
        Enumerate(0, 0);
        var best = solutions.Min(solution => solution.Score);
        var plausible = solutions.Where(solution => solution.Score <= best + plausibleMargin).ToArray();
        return plausible[0].Assignments
            .Where(pair => plausible.All(solution => solution.Assignments.TryGetValue(pair.Key, out var pilot) && pilot == pair.Value))
            .Select(pair => (pair.Key, pair.Value)).ToHashSet();

        void Enumerate(int contact, double total)
        {
            if (contact == size)
            {
                solutions.Add((total, new Dictionary<int, int>(current)));
                return;
            }

            Enumerate(contact + 1, total);
            for (var pilot = 0; pilot < size; pilot++)
            {
                if (scores[contact, pilot] is not double score || !usedPilots.Add(pilot)) continue;
                current[contact] = pilot;
                Enumerate(contact + 1, total + score - unmatchedPenalty);
                current.Remove(contact);
                usedPilots.Remove(pilot);
            }
        }
    }

    [Fact]
    public void EnrichSnapshot_AddsCallsignWhenPilotPositionMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92)
            ]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_ExcludesOwnshipByCidAndConfiguredCallsign()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("OWN1", 60.1, 24.1, 5000, 250, 90, null, "98765"),
             new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5000, 250, 90, null, "12345")],
            new VatsimOwnshipIdentity("98765", "OWN1"));
        Assert.Equal("FIN123", result.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_LeavesSymmetricFormationUnresolved()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 60.1, 24.1, 5000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("RETRO61", 60.1, 24.1, 5000, 250, 90),
             new VatsimPilotCandidate("RETRO62", 60.1, 24.1, 5000, 250, 90)]);
        Assert.All(result.Contacts, contact => Assert.Null(contact.Callsign));
    }

    [Fact]
    public void EnrichSnapshot_DoesNotOverwriteExistingCallsign()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", "SIMCALL", 60.1, 24.1, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92)
            ]);

        Assert.Equal("SIMCALL", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_RejectsDistantPilot()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 61.0, 25.0, 5050, 245, 92)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_RejectsHeadingMismatchWhenPositionMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 0, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 180)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_RejectsSpeedMismatchWhenPositionMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 420, 92)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_AllowsMotionMismatchWhenSimulatorSpeedIsUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 0, 0, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 450, 180)
            ]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_RejectsAmbiguousNearbyPilots()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92),
                new VatsimPilotCandidate("FIN124", 60.1002, 24.1002, 5075, 250, 88)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshot_LeavesIndistinguishableFormationUnresolved()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1000, 24.1000, 5000, 90, 250, now),
                new TrafficContactState("T2", null, 60.1002, 24.1002, 5050, 91, 248, now),
                new TrafficContactState("T3", null, 60.1004, 24.1004, 5100, 92, 246, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("ORANGE1", 60.1000, 24.1000, 5000, 250, 90),
                new VatsimPilotCandidate("ORANGE2", 60.1002, 24.1002, 5050, 248, 91),
                new VatsimPilotCandidate("ORANGE3", 60.1004, 24.1004, 5100, 246, 92)
            ]);

        Assert.All(enriched.Contacts, contact => Assert.Null(contact.Callsign));
    }

    [Fact]
    public void EnrichSnapshot_AssignsFormationWhenGeometryClearlySeparatesPilots()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 300, now),
            [new TrafficContactState("T1", null, 60.100, 24.100, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 60.120, 24.100, 5000, 90, 250, now),
             new TrafficContactState("T3", null, 60.140, 24.100, 5000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("ORANGE1", 60.100, 24.100, 5000, 250, 90),
             new VatsimPilotCandidate("ORANGE2", 60.120, 24.100, 5000, 250, 90),
             new VatsimPilotCandidate("ORANGE3", 60.140, 24.100, 5000, 250, 90)]);
        Assert.Collection(result.Contacts,
            contact => Assert.Equal("ORANGE1", contact.Callsign),
            contact => Assert.Equal("ORANGE2", contact.Callsign),
            contact => Assert.Equal("ORANGE3", contact.Callsign));
    }

    [Fact]
    public void EnrichSnapshot_AssignsSinglePilotOnlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1000, 24.1000, 5000, 90, 250, now),
                new TrafficContactState("T2", null, 60.1002, 24.1002, 5050, 91, 248, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshot(
            snapshot,
            [
                new VatsimPilotCandidate("VIPER2", 60.1001, 24.1001, 5025, 249, 90)
            ]);

        Assert.InRange(enriched.Contacts.Count(contact => contact.Callsign == "VIPER2"), 0, 1);
    }

    [Fact]
    public void EnrichSnapshot_AssignsOnePilotToOnlyTheClearlyMatchingContact()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 300, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 60.2, 24.2, 6000, 270, 100, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5000, 250, 90)]);
        Assert.Equal("FIN123", result.Contacts[0].Callsign);
        Assert.Null(result.Contacts[1].Callsign);
    }

    [Fact]
    public void EnrichSnapshot_PreservesClearPairWhenSeparateFormationSubsetIsAmbiguous()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 60.5, 24.5, 5000, 90, 250, now),
             new TrafficContactState("T3", null, 60.5, 24.5, 5000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5000, 250, 90),
             new VatsimPilotCandidate("RETRO61", 60.5, 24.5, 5000, 250, 90),
             new VatsimPilotCandidate("RETRO62", 60.5, 24.5, 5000, 250, 90)]);

        Assert.Collection(result.Contacts,
            contact => Assert.Equal("FIN123", contact.Callsign),
            contact => Assert.Null(contact.Callsign),
            contact => Assert.Null(contact.Callsign));
    }

    [Fact]
    public void InspectCurrentAssignment_ReportsStablePairAndLocalAmbiguityComponent()
    {
        var now = DateTimeOffset.UtcNow;
        var contacts = new[]
        {
            new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
            new TrafficContactState("T2", null, 60.5, 24.5, 5000, 90, 250, now),
            new TrafficContactState("T3", null, 60.5, 24.5, 5000, 90, 250, now)
        };
        var pilots = new[]
        {
            new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5000, 250, 90),
            new VatsimPilotCandidate("RETRO61", 60.5, 24.5, 5000, 250, 90),
            new VatsimPilotCandidate("RETRO62", 60.5, 24.5, 5000, 250, 90)
        };

        var diagnostics = VatsimCallsignMatcher.InspectCurrentAssignment(contacts, pilots);
        Assert.True(diagnostics["T1"].StableAcrossPlausibleSolutions);
        Assert.Equal("FIN123", diagnostics["T1"].Callsign);
        Assert.False(diagnostics["T2"].StableAcrossPlausibleSolutions);
        Assert.Equal(diagnostics["T2"].ComponentId, diagnostics["T3"].ComponentId);
        Assert.Equal(2, diagnostics["T2"].ComponentContactCount);
        Assert.True(diagnostics["T2"].UnmatchedAlternativeCost > 0);
    }

    [Fact]
    public void EnrichSnapshot_AmbiguousComponentDoesNotSuppressIndependentClearGroup()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T3", null, 61.0, 24.0, 5000, 90, 250, now),
             new TrafficContactState("T4", null, 61.02, 24.0, 6000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("VIPER1", 60.1, 24.1, 5000, 250, 90),
             new VatsimPilotCandidate("VIPER2", 60.1, 24.1, 5000, 250, 90),
             new VatsimPilotCandidate("RETRO1", 61.0, 24.0, 5000, 250, 90),
             new VatsimPilotCandidate("RETRO2", 61.02, 24.0, 6000, 250, 90)]);

        Assert.Null(result.Contacts[0].Callsign);
        Assert.Null(result.Contacts[1].Callsign);
        Assert.Equal("RETRO1", result.Contacts[2].Callsign);
        Assert.Equal("RETRO2", result.Contacts[3].Callsign);
    }

    [Fact]
    public void EnrichSnapshot_UnmatchedAlternativeRejectsWeakEdgeWithoutSuppressingStrongPair()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
             new TrafficContactState("T2", null, 61.0, 24.0, 5000, 90, 250, now)], now);
        var result = VatsimCallsignMatcher.EnrichSnapshot(snapshot,
            [new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5000, 250, 90),
             new VatsimPilotCandidate("WEAK1", 61.023, 24.0, 5000, 250, 90)]);

        Assert.Equal("FIN123", result.Contacts[0].Callsign);
        Assert.Null(result.Contacts[1].Callsign);
    }

    [Fact]
    public void EnrichSnapshot_FormationCanBecomeDistinctWithoutArbitraryIntermediatePermutation()
    {
        var now = DateTimeOffset.UtcNow;
        TrafficSnapshot CreateSnapshot(bool distinct) => new(
            new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
            distinct
                ? [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
                   new TrafficContactState("T2", null, 60.12, 24.1, 5000, 90, 250, now),
                   new TrafficContactState("T3", null, 60.14, 24.1, 5000, 90, 250, now)]
                : [new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now),
                   new TrafficContactState("T2", null, 60.1, 24.1, 5000, 90, 250, now),
                   new TrafficContactState("T3", null, 60.1, 24.1, 5000, 90, 250, now)], now);
        var pilots = new[]
        {
            new VatsimPilotCandidate("RETRO61", 60.1, 24.1, 5000, 250, 90),
            new VatsimPilotCandidate("RETRO62", 60.12, 24.1, 5000, 250, 90),
            new VatsimPilotCandidate("RETRO63", 60.14, 24.1, 5000, 250, 90)
        };

        var ambiguous = VatsimCallsignMatcher.EnrichSnapshot(CreateSnapshot(false), pilots);
        Assert.All(ambiguous.Contacts, contact => Assert.Null(contact.Callsign));
        var distinguishable = VatsimCallsignMatcher.EnrichSnapshot(CreateSnapshot(true), pilots);
        Assert.Collection(distinguishable.Contacts,
            contact => Assert.Equal("RETRO61", contact.Callsign),
            contact => Assert.Equal("RETRO62", contact.Callsign),
            contact => Assert.Equal("RETRO63", contact.Callsign));
        Assert.Equal(3, distinguishable.Contacts.Select(contact => contact.Callsign).Distinct().Count());
    }

    [Fact]
    public void EnrichSnapshotFromHistory_MatchesAgainstVatsimTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var vatsimTime = now.AddSeconds(-18);
        var historical = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, vatsimTime),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, vatsimTime)
            ],
            vatsimTime);
        var current = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.5, 24.5, 5000, 90, 250, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            current,
            [historical, current],
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92, vatsimTime)
            ]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_RejectsWhenTimestampIsOutsideHistoryWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var vatsimTime = now.AddSeconds(-35);
        var historical = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now.AddSeconds(-8)),
            [
                new TrafficContactState("T1", null, 60.1, 24.1, 5000, 90, 250, now.AddSeconds(-8))
            ],
            now.AddSeconds(-8));
        var current = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1001, 24.1001, 5050, 92, 245, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            current,
            [historical, current],
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92, vatsimTime)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_UsesCurrentSnapshotOnlyWhenVatsimTimestampIsUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now),
            [
                new TrafficContactState("T1", null, 60.1001, 24.1001, 5050, 92, 245, now)
            ],
            now);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            current,
            [current],
            [
                new VatsimPilotCandidate("FIN123", 60.1001, 24.1001, 5050, 245, 92, null)
            ]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_InterpolatesFastMovingContactBetweenSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var firstTime = now.AddSeconds(-20);
        var targetTime = now.AddSeconds(-15);
        var secondTime = now.AddSeconds(-10);

        static TrafficSnapshot Snapshot(DateTimeOffset timestamp, double latitude) =>
            new(
                new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, timestamp),
                [new TrafficContactState("T1", null, latitude, 24.0, 5000, 90, 450, timestamp)],
                timestamp);

        var current = Snapshot(now, 61.0);
        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            current,
            [Snapshot(firstTime, 60.0), Snapshot(secondTime, 60.2), current],
            [new VatsimPilotCandidate("FIN123", 60.1, 24.0, 5000, 450, 90, targetTime)]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_UsesConservativeKinematicFallbackForOffsetMsfsTraffic()
    {
        var now = DateTimeOffset.UtcNow;
        var feedTime = now.AddSeconds(-2);
        var historical = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, feedTime),
            [new TrafficContactState("T1", null, 60.05, 24.0, 5000, 90, 250, feedTime)],
            feedTime);
        var current = historical with
        {
            Timestamp = now,
            Contacts = [historical.Contacts.Single() with { Timestamp = now }]
        };

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            current,
            [historical, current],
            [new VatsimPilotCandidate("FIN123", 60.10, 24.0, 5000, 250, 90, feedTime)]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_RejectsFallbackWhenMotionDoesNotMatch()
    {
        var now = DateTimeOffset.UtcNow;
        var feedTime = now.AddSeconds(-2);
        var contact = new TrafficContactState("T1", null, 60.05, 24.0, 5000, 90, 250, feedTime);
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, feedTime),
            [contact],
            feedTime);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            snapshot with { Timestamp = now },
            [snapshot],
            [new VatsimPilotCandidate("FIN123", 60.10, 24.0, 5000, 250, 270, feedTime)]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_RejectsAmbiguousKinematicFallback()
    {
        var now = DateTimeOffset.UtcNow;
        var feedTime = now.AddSeconds(-2);
        var contact = new TrafficContactState("T1", null, 60.05, 24.0, 5000, 90, 250, feedTime);
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, feedTime),
            [contact],
            feedTime);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            snapshot,
            [snapshot],
            [
                new VatsimPilotCandidate("FIN123", 60.10, 24.0, 5000, 250, 90, feedTime),
                new VatsimPilotCandidate("FIN124", 60.105, 24.0, 5000, 250, 90, feedTime)
            ]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_AllowsUniqueNearFallbackWhenMsfsMotionIsUnavailable()
    {
        var now = DateTimeOffset.UtcNow;
        var feedTime = now.AddSeconds(-2);
        var contact = new TrafficContactState("T1", null, 60.05, 24.0, 5000, null, null, feedTime);
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, feedTime),
            [contact],
            feedTime);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            snapshot,
            [snapshot],
            [new VatsimPilotCandidate("FIN123", 60.075, 24.0, 5050, 250, 90, feedTime)]);

        Assert.Equal("FIN123", enriched.Contacts.Single().Callsign);
    }

    [Fact]
    public void EnrichSnapshotFromHistory_RejectsMotionlessFallbackOutsideNearEnvelope()
    {
        var now = DateTimeOffset.UtcNow;
        var feedTime = now.AddSeconds(-2);
        var contact = new TrafficContactState("T1", null, 60.05, 24.0, 5000, null, null, feedTime);
        var snapshot = new TrafficSnapshot(
            new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, feedTime),
            [contact],
            feedTime);

        var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(
            snapshot,
            [snapshot],
            [new VatsimPilotCandidate("FIN123", 60.10, 24.0, 5050, 250, 90, feedTime)]);

        Assert.Null(enriched.Contacts.Single().Callsign);
    }
}
