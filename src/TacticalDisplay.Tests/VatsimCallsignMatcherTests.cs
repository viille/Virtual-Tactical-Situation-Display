using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class VatsimCallsignMatcherTests
{
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
    public void EnrichSnapshot_AssignsUniquePilotsWhenFormationContactsAreClose()
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

        Assert.Collection(
            enriched.Contacts,
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

        Assert.Single(enriched.Contacts, static contact => contact.Callsign == "VIPER2");
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
}
