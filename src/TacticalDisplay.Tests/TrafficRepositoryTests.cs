using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TrafficRepositoryTests
{
    [Fact]
    public void ApplySnapshot_RemovesContactAboveMaximumTrackedAltitude()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings
        {
            MaxTrackedAltitudeFt = 80000
        };
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 21000, 0, 300, now);

        repository.ApplySnapshot(
            new TrafficSnapshot(
                ownship,
                [
                    new TrafficContactState("T1", null, 60.1, 24.0, 5000, 180, 250, now)
                ],
                now),
            classification,
            settings);

        repository.ApplySnapshot(
            new TrafficSnapshot(
                ownship with { Timestamp = now.AddSeconds(1) },
                [
                    new TrafficContactState("T1", null, 60.1, 24.0, 100700, 180, 250, now.AddSeconds(1))
                ],
                now.AddSeconds(1)),
            classification,
            settings);

        Assert.Equal(0, repository.Count);
        Assert.Empty(repository.BuildPicture(settings).Targets);
    }

    [Fact]
    public void ApplySnapshot_KeepsLastKnownCallsignWhenNextSnapshotDoesNotIncludeIt()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);

        repository.ApplySnapshot(
            new TrafficSnapshot(
                ownship,
                [
                    new TrafficContactState("T1", "FIN123", 60.1, 24.0, 5000, 180, 250, now)
                ],
                now),
            classification,
            settings);

        repository.ApplySnapshot(
            new TrafficSnapshot(
                ownship with { Timestamp = now.AddSeconds(1) },
                [
                    new TrafficContactState("T1", null, 60.11, 24.0, 5000, 180, 250, now.AddSeconds(1))
                ],
                now.AddSeconds(1)),
            classification,
            settings);

        var picture = repository.BuildPicture(settings);

        Assert.Equal("FIN123", picture.Targets.Single().DisplayName);
    }

    [Fact]
    public void ApplySnapshot_DelaysAndThenSuppressesStationaryContactsWithoutCallsign()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);

        static TrafficSnapshot Snapshot(OwnshipState ownship, DateTimeOffset timestamp) =>
            new(
                ownship with { Timestamp = timestamp },
                [
                    new TrafficContactState("T1", null, 60.1, 24.0, 5000, 180, 0, timestamp),
                    new TrafficContactState("T2", null, 60.10001, 24.0, 5000, 180, 0, timestamp)
                ],
                timestamp);

        repository.ApplySnapshot(Snapshot(ownship, now), classification, settings);
        Assert.Equal(2, repository.Count);

        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(6)), classification, settings);
        Assert.Equal(0, repository.Count);
    }

    [Fact]
    public void ApplySnapshot_SuppressesSingleStationaryContactAfterGracePeriod()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);

        static TrafficSnapshot Snapshot(OwnshipState ownship, DateTimeOffset timestamp) =>
            new(
                ownship with { Timestamp = timestamp },
                [new TrafficContactState("T1", null, 60.1, 24.0, 5000, 180, 0, timestamp)],
                timestamp);

        repository.ApplySnapshot(Snapshot(ownship, now), classification, settings);
        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(6)), classification, settings);

        Assert.Equal(0, repository.Count);
    }

    [Fact]
    public void ApplySnapshot_RevealsSuppressedContactWhenCallsignAppears()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);

        static TrafficSnapshot Snapshot(OwnshipState ownship, DateTimeOffset timestamp, string? callsign = null) =>
            new(
                ownship with { Timestamp = timestamp },
                [
                    new TrafficContactState("T1", callsign, 60.1, 24.0, 5000, 180, 0, timestamp),
                    new TrafficContactState("T2", null, 60.10001, 24.0, 5000, 180, 0, timestamp)
                ],
                timestamp);

        repository.ApplySnapshot(Snapshot(ownship, now), classification, settings);
        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(6)), classification, settings);
        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(7), "FIN123"), classification, settings);

        Assert.Equal(1, repository.Count);
        Assert.Equal("FIN123", repository.BuildPicture(settings).Targets.Single().DisplayName);
    }

    [Fact]
    public void ApplySnapshot_RevealsSuppressedContactWhenItMoves()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);

        static TrafficSnapshot Snapshot(OwnshipState ownship, DateTimeOffset timestamp, double latitude) =>
            new(
                ownship with { Timestamp = timestamp },
                [
                    new TrafficContactState("T1", null, latitude, 24.0, 5000, 180, 0, timestamp),
                    new TrafficContactState("T2", null, 60.10001, 24.0, 5000, 180, 0, timestamp)
                ],
                timestamp);

        repository.ApplySnapshot(Snapshot(ownship, now, 60.1), classification, settings);
        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(6), 60.1), classification, settings);
        repository.ApplySnapshot(Snapshot(ownship, now.AddSeconds(7), 60.101), classification, settings);

        Assert.Equal(1, repository.Count);
    }
}
