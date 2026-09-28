using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TrafficRepositoryTests
{
    [Theory]
    [InlineData(165.0, "C+165")]
    [InlineData(0.0, "C+0")]
    [InlineData(-42.0, "C-42")]
    [InlineData(null, "C---")]
    public void FormatClosureLabel_IdentifiesClosure(double? closure, string expected) =>
        Assert.Equal(expected, TrafficRepository.FormatClosureLabel(closure));

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(-40)]
    [InlineData(800)]
    public void Closure_UsesRelativeRangeRegression(double expectedKt)
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 250, now);
        var startRangeNm = 10.0;

        for (var second = 0; second <= 4; second++)
        {
            var timestamp = now.AddSeconds(second);
            var range = startRangeNm - expectedKt * second / 3600.0;
            var latitude = range / 60.0;
            var sampleOwnship = ownship with { Timestamp = timestamp };
            var contact = new TrafficContactState("T1", "FIN123", latitude, 0, 5000, 180, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(sampleOwnship, [contact], timestamp), classification, settings);
        }

        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.NotNull(target.ClosureKt);
        Assert.InRange(target.ClosureKt!.Value, expectedKt - 1.0, expectedKt + 1.0);
        Assert.Equal("position", repository.GetTrackedContact("T1")!.ClosureSource);
    }

    [Fact]
    public void Closure_PositionHistoryOverridesIncorrectVelocityProjection()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);

        for (var second = 0; second <= 4; second++)
        {
            var timestamp = now.AddSeconds(second);
            var contact = new TrafficContactState("T1", "FIN123", 10.0 / 60.0, 0, 5000, 270, 200, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.InRange(target.ClosureKt!.Value, -0.01, 0.01);
        Assert.Equal("position", repository.GetTrackedContact("T1")!.ClosureSource);
    }

    [Fact]
    public void Closure_UsesVelocityFallbackUntilPositionHistoryIsReady()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);
        var contact = new TrafficContactState("T1", "FIN123", 10.0 / 60.0, 0, 5000, 180, 250, now);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], now), classification, settings);

        Assert.InRange(repository.BuildPicture(settings).Targets.Single().ClosureKt!.Value, 249.9, 250.1);
        Assert.Equal("vector", repository.GetTrackedContact("T1")!.ClosureSource);
    }

    [Fact]
    public void Closure_SmoothsRangeJitterWithRegression()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);
        var jitter = new[] { 0.0000, 0.0002, -0.0002, 0.0001, -0.0001, 0.0000, 0.0001 };

        for (var second = 0; second < jitter.Length; second++)
        {
            var timestamp = now.AddSeconds(second);
            var latitude = 10.0 / 60.0 + jitter[second];
            var contact = new TrafficContactState("T1", "FIN123", latitude, 0, 5000, 270, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        Assert.InRange(System.Math.Abs(repository.BuildPicture(settings).Targets.Single().ClosureKt!.Value), 0, 80);
    }

    [Fact]
    public void Closure_ResetsHistoryAndLastKnownValueOnTeleport()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);

        for (var second = 0; second <= 3; second++)
        {
            var timestamp = now.AddSeconds(second);
            var contact = new TrafficContactState("T1", "FIN123", 10.0 / 60.0, 0, 5000, 270, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var teleportTime = now.AddSeconds(4);
        var teleported = new TrafficContactState("T1", "FIN123", 20.0 / 60.0, 0, 5000, 270, 250, teleportTime);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = teleportTime }, [teleported], teleportTime), classification, settings);

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Null(tracked.LastKnownClosureKt);
        Assert.Null(tracked.PositionClosureKt);
        Assert.Null(tracked.VectorClosureKt);
        Assert.Equal("unavailable", tracked.ClosureSource);
        var selected = repository.BuildPicture(settings).Targets.Single().ClosureKt;
        Assert.True(selected is null || System.Math.Abs(selected.Value) < 1500);
    }

    [Fact]
    public void Closure_AcceptsHighSpeedHeadOnWithoutTeleportReset()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, -5.0 / 60.0, 5000, 90, 800, now);

        for (var second = 0; second <= 4; second++)
        {
            var timestamp = now.AddSeconds(second);
            var movementNm = 800 * second / 3600.0;
            var sampleOwnship = ownship with
            {
                LongitudeDeg = (-5 + movementNm) / 60.0,
                Timestamp = timestamp
            };
            var contact = new TrafficContactState(
                "T1",
                "FAST1",
                0,
                (5 - movementNm) / 60.0,
                5000,
                270,
                800,
                timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(sampleOwnship, [contact], timestamp), classification, settings);
        }

        var tracked = repository.GetTrackedContact("T1")!;
        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.InRange(target.ClosureKt!.Value, 1590, 1610);
        Assert.Equal("position", tracked.ClosureSource);
        Assert.True(tracked.History.Count >= 5);
        Assert.InRange(tracked.Current.SpeedKt!.Value, 799, 801);
        Assert.InRange(tracked.VectorClosureKt!.Value, 1590, 1610);
        Assert.InRange(tracked.PositionClosureKt!.Value, 1590, 1610);
    }

    [Fact]
    public void Closure_DetectsTargetTeleportWhenRangeStaysSame()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 0, now);
        var east = new TrafficContactState("T1", "TELE1", 0, 10.0 / 60.0, 5000, 270, 250, now);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [east], now), classification, settings);
        _ = repository.BuildPicture(settings);

        var teleportTime = now.AddSeconds(1);
        var west = east with { LongitudeDeg = -10.0 / 60.0, Timestamp = teleportTime };
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = teleportTime }, [west], teleportTime), classification, settings);

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Null(tracked.LastKnownClosureKt);
        Assert.Null(tracked.PositionClosureKt);
        Assert.Null(tracked.VectorClosureKt);
        Assert.Equal("unavailable", tracked.ClosureSource);
        Assert.Single(tracked.History);
    }

    [Fact]
    public void Closure_InitializesRelativeRangeOnFirstSampleAndClearsStaleVectorData()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);
        var firstContact = new TrafficContactState("T1", "FIN123", 10.0 / 60.0, 0, 5000, 270, 250, now);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [firstContact], now), classification, settings);
        var tracked = repository.GetTrackedContact("T1")!;
        Assert.InRange(tracked.RelativeRangeNm!.Value, 9.99, 10.02);
        _ = repository.BuildPicture(settings);
        Assert.NotNull(tracked.VectorClosureKt);
        Assert.NotNull(tracked.LastKnownClosureKt);

        var timestamp = now.AddSeconds(1);
        var missingVelocity = firstContact with { SpeedKt = null, HeadingDeg = null, Timestamp = timestamp };
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [missingVelocity], timestamp), classification, settings);
        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.Null(tracked.VectorClosureKt);
        Assert.Null(tracked.LastKnownClosureKt);
        Assert.Null(target.ClosureKt);
        Assert.Equal("unavailable", tracked.ClosureSource);
    }

    [Fact]
    public void Closure_FirstSampleContributesToMinimumRegressionWindow()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 250, now);

        for (var second = 0; second <= 2; second++)
        {
            var timestamp = now.AddSeconds(second);
            var latitude = (10 - 50 * second / 3600.0) / 60.0;
            var contact = new TrafficContactState("T1", "FIN123", latitude, 0, 5000, 180, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.InRange(target.ClosureKt!.Value, 49, 51);
        Assert.Equal("position", repository.GetTrackedContact("T1")!.ClosureSource);
    }

    [Fact]
    public void Closure_DoesNotReuseHistoryAfterContactRemovalAndSameIdReturns()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, now);

        for (var second = 0; second <= 3; second++)
        {
            var timestamp = now.AddSeconds(second);
            var contact = new TrafficContactState("T1", "FIN123", 10.0 / 60.0, 0, 5000, 270, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var newTime = now.AddSeconds(20);
        var newContact = new TrafficContactState("T1", "NEW456", 20.0 / 60.0, 0, 5000, 270, 250, newTime);
        var settingsWithShortRemoval = new TacticalDisplaySettings { RemoveAfterSeconds = 10 };
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = newTime }, [newContact], newTime), classification, settingsWithShortRemoval);
        var nextTime = newTime.AddSeconds(0.5);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = nextTime }, [newContact with { Timestamp = nextTime }], nextTime), classification, settingsWithShortRemoval);

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Null(tracked.PositionClosureKt);
        _ = repository.BuildPicture(settings);
        Assert.Equal("vector", tracked.ClosureSource);
    }

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
