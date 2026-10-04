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
    [InlineData(200)]
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
    public void Closure_TransitionsFromVectorToPositionWithoutAnInstantaneousJump()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        double? ReadClosure(double second)
        {
            var timestamp = start.AddSeconds(second);
            var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 300, timestamp);
            var target = new TrafficContactState("T1", "BLEND1", 0, 10.0 / 60.0, 5000, 270, 200, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [target], timestamp), classification, settings);
            return repository.BuildPicture(settings).Targets.Single().ClosureKt;
        }

        _ = ReadClosure(0);
        _ = ReadClosure(0.5);
        _ = ReadClosure(1);
        var transitionStart = ReadClosure(1.5);
        Assert.InRange(transitionStart!.Value, 499, 501);
        Assert.Equal("vector-position-transition", repository.GetTrackedContact("T1")!.ClosureSource);
        var transition = ReadClosure(2);
        Assert.InRange(transition!.Value, 320, 345);
        var final = ReadClosure(3);
        Assert.InRange(final!.Value, -0.01, 0.01);
        Assert.Equal("position", repository.GetTrackedContact("T1")!.ClosureSource);
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
    public void Closure_RecencyWeightedRegressionRespondsToRecentManeuver()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 0, now);

        for (var sample = 0; sample <= 8; sample++)
        {
            var elapsedSeconds = sample * 0.5;
            var timestamp = now.AddSeconds(elapsedSeconds);
            var distanceClosedNm = (100 * System.Math.Min(elapsedSeconds, 2) + 500 * System.Math.Max(0, elapsedSeconds - 2)) / 3600.0;
            var contact = new TrafficContactState(
                "T1",
                "MANEUVER1",
                (20 - distanceClosedNm) / 60.0,
                0,
                5000,
                180,
                250,
                timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var tracked = repository.GetTrackedContact("T1")!;
        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.InRange(target.ClosureKt!.Value, 450, 510);
        const double equalWeightClosureKt = 300;
        Assert.True(
            System.Math.Abs(500 - target.ClosureKt.Value) < System.Math.Abs(500 - equalWeightClosureKt),
            $"Expected weighted closure {target.ClosureKt.Value:0.0} kt to be closer to the recent 500 kt trend than the equal-weight estimate.");
        Assert.Equal("position", tracked.ClosureSource);
    }

    [Fact]
    public void Closure_OldRangeTransientHasReducedInfluence()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 0, now);

        for (var sample = 0; sample <= 8; sample++)
        {
            var elapsedSeconds = sample * 0.5;
            var timestamp = now.AddSeconds(elapsedSeconds);
            var rangeNm = 20 - 200 * elapsedSeconds / 3600.0;
            if (sample == 0)
            {
                rangeNm += 0.02;
            }

            var contact = new TrafficContactState("T1", "TRANSIENT1", rangeNm / 60.0, 0, 5000, 180, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var closure = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value;
        const double equalWeightClosureKt = 209.6;
        Assert.True(
            System.Math.Abs(closure - 200) < System.Math.Abs(equalWeightClosureKt - 200),
            $"Expected the old transient to have less influence than with equal weighting; got {closure:0.0} kt.");
    }

    [Fact]
    public void Closure_NewestModestRangeErrorDoesNotDominateWeightedRegression()
    {
        var repository = new TrafficRepository();
        var now = DateTimeOffset.UtcNow;
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 90, 0, now);

        for (var sample = 0; sample <= 8; sample++)
        {
            var elapsedSeconds = sample * 0.5;
            var timestamp = now.AddSeconds(elapsedSeconds);
            var rangeNm = 20 - 200 * elapsedSeconds / 3600.0;
            if (sample == 8)
            {
                rangeNm += 0.02;
            }

            var contact = new TrafficContactState("T1", "NEWESTERR1", rangeNm / 60.0, 0, 5000, 180, 250, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [contact], timestamp), classification, settings);
        }

        var closure = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value;
        Assert.InRange(closure, 160, 240);
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
    public void ApplySnapshot_ExplicitCallsignRevocationClearsLastKnownValue()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 0, 250, now);
        repository.ApplySnapshot(new TrafficSnapshot(ownship,
            [new TrafficContactState("T1", "FIN123", 60.1, 24, 5000, 180, 250, now)], now), classification, settings);
        var later = now.AddSeconds(1);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = later },
            [new TrafficContactState("T1", null, 60.1, 24, 5000, 180, 250, later, CallsignRevoked: true)], later), classification, settings);
        Assert.Null(repository.GetTrackedContact("T1")!.LastKnownCallsign);
        Assert.Equal("T1", Assert.Single(repository.BuildPicture(settings).Targets).DisplayName);
    }

    [Fact]
    public void ApplySnapshot_ReusedObjectGenerationDoesNotInheritCallsignOrClosureHistory()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 0, 250, now);
        repository.ApplySnapshot(new TrafficSnapshot(ownship,
            [new TrafficContactState("T1", "FIN123", 60.1, 24, 5000, 180, 250, now, Generation: 11)], now), classification, settings);
        var later = now.AddSeconds(1);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = later },
            [new TrafficContactState("T1", null, 60.2, 24, 5000, 180, 250, later, Generation: 12)], later), classification, settings);
        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Null(tracked.LastKnownCallsign);
        Assert.Single(tracked.History);
        Assert.Equal(12, tracked.Generation);
    }

    [Fact]
    public void ApplySnapshot_NewSimConnectSessionClearsContactsEvenWhenObjectIdIsReused()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 0, 250, now, SessionGeneration: 7);
        var contact = new TrafficContactState("T1", "FIN123", 60.1, 24, 5000, 180, 250, now, Generation: 3);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], now), classification, settings);
        Assert.Equal("FIN123", repository.GetTrackedContact("T1")!.LastKnownCallsign);

        var reconnectTime = now.AddSeconds(1);
        var reconnectedOwnship = ownship with { Timestamp = reconnectTime, SessionGeneration = 8 };
        var reusedObject = contact with
        {
            Callsign = null,
            Timestamp = reconnectTime,
            Generation = 0,
            LatitudeDeg = 61
        };
        repository.ApplySnapshot(new TrafficSnapshot(reconnectedOwnship, [reusedObject], reconnectTime), classification, settings);

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Null(tracked.LastKnownCallsign);
        Assert.Single(tracked.History);
        Assert.Equal(0, tracked.Generation);
    }

    [Fact]
    public void Closure_AlignsAsynchronousOwnshipAndTargetEpochs()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        OwnshipState OwnAt(double second)
        {
            var timestamp = start.AddSeconds(second);
            return new OwnshipState("OWN", 0, 300 * second / 3600.0 / 60.0, 5000, 90, 300, timestamp);
        }

        repository.ApplySnapshot(new TrafficSnapshot(OwnAt(0), [], start), classification, settings);
        var targetTimes = new[] { 0.2, 1.8, 2.2, 3.8, 4.2 };
        var ownTimes = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        for (var index = 0; index < targetTimes.Length; index++)
        {
            var targetTime = targetTimes[index];
            var ownTime = ownTimes[index];
            var ownship = OwnAt(ownTime);
            var ownLongitudeAtTarget = 300 * targetTime / 3600.0 / 60.0;
            var rangeNm = 10 - 100 * targetTime / 3600.0;
            var contact = new TrafficContactState("T1", "ASYNC1", 0,
                ownLongitudeAtTarget + rangeNm / 60.0, 5000, 270, 250, start.AddSeconds(targetTime));
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], ownship.Timestamp), classification, settings);
        }

        var closure = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt;
        Assert.NotNull(closure);
        Assert.InRange(closure!.Value, 98, 102);
    }

    [Fact]
    public void Closure_UsesLongerWindowForIrregularSamplesWithoutChangingConstantTrend()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 0, null, start);
        foreach (var second in new[] { 0.0, 0.5, 2.0, 2.5, 4.0 })
        {
            var timestamp = start.AddSeconds(second);
            var rangeNm = 10 - 120 * second / 3600.0;
            var target = new TrafficContactState("T1", "IRREG1", rangeNm / 60.0, 0, 5000, null, null, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [target], timestamp), classification, settings);
        }

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.InRange(Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value, 118, 122);
        Assert.Equal(4, tracked.RegressionWindowSeconds);
        Assert.InRange(tracked.LargestSampleGapSeconds, 1.49, 1.51);

        var regularRepository = new TrafficRepository();
        for (var sample = 0; sample <= 4; sample++)
        {
            var second = sample * 0.5;
            var timestamp = start.AddSeconds(second);
            var rangeNm = 10 - 120 * second / 3600.0;
            var target = new TrafficContactState("T1", "REGULAR1", rangeNm / 60.0, 0, 5000, null, null, timestamp);
            regularRepository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [target], timestamp), classification, settings);
        }

        _ = regularRepository.BuildPicture(settings);
        Assert.True(tracked.PositionQualityScore < regularRepository.GetTrackedContact("T1")!.PositionQualityScore,
            "Extending the selected window for irregular data must not itself increase the quality score.");
    }

    [Fact]
    public void Closure_RejectsSinglePositionOutlierFromRegression()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 0, null, start);
        for (var sample = 0; sample <= 8; sample++)
        {
            var second = sample * 0.5;
            var timestamp = start.AddSeconds(second);
            var rangeNm = 10 - 120 * second / 3600.0;
            if (sample == 4) rangeNm += 0.3;
            var target = new TrafficContactState("T1", "OUTLIER1", rangeNm / 60.0, 0, 5000, null, null, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [target], timestamp), classification, settings);
        }

        var tracked = repository.GetTrackedContact("T1")!;
        var closure = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value;
        Assert.InRange(closure, 115, 125);
        Assert.Equal(1, tracked.RegressionOutliersRemoved);
    }

    [Fact]
    public void Closure_RespondsToOwnshipTurnUsingAlignedPositions()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        double? closureAtTurn = null;
        for (var sample = 0; sample <= 12; sample++)
        {
            var second = sample * 0.5;
            var timestamp = start.AddSeconds(second);
            var northNm = 300 * System.Math.Min(second, 2) / 3600.0;
            var eastNm = 300 * System.Math.Max(0, second - 2) / 3600.0;
            var ownship = new OwnshipState("OWN", northNm / 60.0, eastNm / 60.0, 5000, 0, null, timestamp);
            var target = new TrafficContactState("T1", "OWNTURN1", 0, 0.5 / 60.0, 5000, null, null, timestamp);
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [target], timestamp), classification, settings);
            if (sample == 4) closureAtTurn = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt;
        }

        var finalClosure = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value;
        Assert.NotNull(closureAtTurn);
        Assert.True(finalClosure > closureAtTurn, $"Expected ownship turn to change radial closure from {closureAtTurn:0.0} kt to {finalClosure:0.0} kt.");
        Assert.InRange(finalClosure, 180, 340);
    }

    [Fact]
    public void Closure_ReportsVectorPositionDisagreementAndKeepsPositionEstimate()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 0, 0, 5000, 0, 0, start, GroundTrackDeg: 0);
        for (var second = 0; second <= 4; second++)
        {
            var timestamp = start.AddSeconds(second);
            var target = new TrafficContactState(
                "T1", "DISAGREE1", (10 + 100 * second / 3600.0) / 60.0, 0, 5000,
                180, 300, timestamp, GroundTrackDeg: 180);
            repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = timestamp }, [target], timestamp), classification, settings);
        }

        var tracked = repository.GetTrackedContact("T1")!;
        var selected = Assert.Single(repository.BuildPicture(settings).Targets).ClosureKt!.Value;
        Assert.InRange(tracked.PositionClosureKt!.Value, -102, -98);
        Assert.InRange(tracked.VectorClosureKt!.Value, 299, 301);
        Assert.InRange(tracked.ClosureDisagreementKt!.Value, 397, 403);
        Assert.Equal("position", tracked.ClosureSource);
        Assert.InRange(selected, -102, -98);
    }

    [Fact]
    public void Closure_DoesNotPublishGeometryWhenTimestampSkewExceedsAlignmentLimit()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 90, 250, start);
        var contact = new TrafficContactState("T1", "STALE1", 60.1, 24, 5000, 180, 250, start.AddSeconds(2));
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], contact.Timestamp), classification, settings);
        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Equal("unavailable", tracked.AlignmentMethod);
        Assert.Null(tracked.RelativeRangeNm);
        Assert.Null(tracked.PositionClosureKt);
        Assert.Empty(repository.BuildPicture(settings).Targets);
    }

    [Fact]
    public void Closure_UsesBoundedExtrapolationAtFourTenthsSecondSkew()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 90, 300, start, GroundTrackDeg: 90);
        var contact = new TrafficContactState("T1", "SKEW04", 60.1, 24, 5000, 180, 250, start.AddMilliseconds(400));
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], contact.Timestamp), new ClassificationConfig(), settings);

        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Equal("extrapolation", tracked.AlignmentMethod);
        Assert.True(tracked.RangeSampleAccepted);
        Assert.Equal("accepted-aligned", tracked.RangeSampleDecision);
        Assert.Single(repository.BuildPicture(settings).Targets);
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(1.0)]
    public void Display_KeepsFreshTargetVisibleWhenClosureAlignmentIsUnavailable(double skewSeconds)
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 90, 250, start);
        var targetTime = start.AddSeconds(skewSeconds);
        var contact = new TrafficContactState("T1", "SKEW1", 60.1, 24, 5000, 180, 250, targetTime);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], targetTime), new ClassificationConfig(), settings);

        var tracked = repository.GetTrackedContact("T1")!;
        var target = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.Equal("unavailable", tracked.AlignmentMethod);
        Assert.Equal("degraded-latest-ownship", tracked.DisplayGeometryMode);
        Assert.Equal(skewSeconds, tracked.OwnshipSampleAgeSeconds, 3);
        Assert.Equal(0, tracked.TargetSampleAgeSeconds);
        Assert.False(tracked.RangeSampleAccepted);
        Assert.Equal("rejected-alignment-unavailable", tracked.RangeSampleDecision);
        Assert.Null(tracked.RelativeRangeNm);
        Assert.Equal("T1", target.Id);
        Assert.Null(tracked.PositionClosureKt);
    }

    [Fact]
    public void Display_RetainsLastAlignedGeometryBrieflyThenExpiresIt()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 90, 250, start);
        var aligned = new TrafficContactState("T1", "RETAIN1", 60.1, 24, 5000, 180, 250, start);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [aligned], start), classification, settings);
        var alignedRange = Assert.Single(repository.BuildPicture(settings).Targets).RangeNm;

        var hiccupTime = start.AddSeconds(1.2);
        var displacedRawTarget = aligned with { LatitudeDeg = 60.2, Timestamp = hiccupTime };
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [displacedRawTarget], hiccupTime), classification, settings);
        var tracked = repository.GetTrackedContact("T1")!;
        var retained = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.Equal("retained-last-valid", tracked.DisplayGeometryMode);
        Assert.False(tracked.RangeSampleAccepted);
        Assert.Equal(alignedRange, retained.RangeNm, 3);
        Assert.Null(tracked.PositionClosureKt);

        var expiredTime = start.AddSeconds(2.1);
        var stillFreshTarget = displacedRawTarget with { Timestamp = expiredTime };
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [stillFreshTarget], expiredTime), classification, settings);
        Assert.Equal("unavailable", tracked.DisplayGeometryMode);
        Assert.Empty(repository.BuildPicture(settings).Targets);
    }

    [Fact]
    public void Closure_ResumesCleanlyAfterTemporaryAlignmentHiccup()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings();
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        void Apply(double ownshipSeconds, double targetSeconds)
        {
            var ownshipTime = start.AddSeconds(ownshipSeconds);
            var targetTime = start.AddSeconds(targetSeconds);
            var ownshipLongitude = 300 * ownshipSeconds / 3600.0 / 60.0;
            var range = 10 - 100 * targetSeconds / 3600.0;
            var ownship = new OwnshipState("OWN", 0, ownshipLongitude, 5000, 90, 300, ownshipTime);
            var contact = new TrafficContactState("T1", "RECOVER1", 0, ownshipLongitude + range / 60.0, 5000, 270, 250, targetTime);
            repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], targetTime), classification, settings);
        }

        Apply(0, 0);
        Apply(0.5, 0.5);
        Apply(1.0, 1.6); // >500 ms skew: visible via degraded geometry, excluded from closure history.
        var tracked = repository.GetTrackedContact("T1")!;
        Assert.Equal("rejected-alignment-unavailable", tracked.RangeSampleDecision);
        Apply(2.0, 2.0);
        Apply(2.5, 2.5);
        Apply(3.0, 3.0);
        Apply(3.5, 3.5);

        var resumed = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.Equal("exact", tracked.AlignmentMethod);
        Assert.Equal("accepted-aligned", tracked.RangeSampleDecision);
        Assert.InRange(resumed.ClosureKt!.Value, 98, 102);
        Assert.Equal("position", tracked.ClosureSource);
        Assert.Equal(6, tracked.RegressionSampleCount);
    }

    [Fact]
    public void Display_DegradedGeometryStillHonorsStaleAndRemovalDeadlines()
    {
        var repository = new TrafficRepository();
        var settings = new TacticalDisplaySettings { StaleSeconds = 2, RemoveAfterSeconds = 5 };
        var classification = new ClassificationConfig();
        var start = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60, 24, 5000, 90, 250, start);
        var contact = new TrafficContactState("T1", "STALE1", 60.1, 24, 5000, 180, 250, start);
        repository.ApplySnapshot(new TrafficSnapshot(ownship, [contact], start), classification, settings);

        var staleTime = start.AddSeconds(3);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = staleTime }, [], staleTime), classification, settings);
        var stale = Assert.Single(repository.BuildPicture(settings).Targets);
        Assert.True(stale.IsStale);

        var removedTime = start.AddSeconds(6);
        repository.ApplySnapshot(new TrafficSnapshot(ownship with { Timestamp = removedTime }, [], removedTime), classification, settings);
        Assert.Empty(repository.BuildPicture(settings).Targets);
        Assert.Equal(0, repository.Count);
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
