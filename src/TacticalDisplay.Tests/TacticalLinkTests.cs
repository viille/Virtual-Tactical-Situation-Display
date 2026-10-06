using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TacticalLinkTests
{
    [Fact]
    public void PeerInterestResolverFiltersGlobalPresenceByConfiguredRadius()
    {
        var positions = new Dictionary<string, TacticalTelemetry>
        {
            ["local"] = Telemetry(60, 25),
            ["near"] = Telemetry(60.1, 25),
            ["far"] = Telemetry(64, 25)
        };

        var peers = new PeerInterestResolver().Resolve("local", positions, 200);

        Assert.Contains("near", peers);
        Assert.DoesNotContain("far", peers);
        Assert.DoesNotContain("local", peers);
    }

    [Fact]
    public void TrafficDeduplicatorRequiresStableStrongOneToOneEvidence()
    {
        var deduplicator = new TrafficDeduplicator();
        var direct = new[] { Contact("tl:1", 60, 25, 25000, 300) };
        var firstSim = Contact("sim:1", 60.0003, 25.0003, 25050, 301);
        var secondSim = Contact("sim:2", 60.0003, 25.0003, 25050, 301);

        for (var sample = 0; sample < 2; sample++)
        {
            var remaining = deduplicator.SuppressDuplicates([firstSim, secondSim], direct);
            Assert.Equal(2, remaining.Count);
        }

        var confirmed = deduplicator.SuppressDuplicates([firstSim, secondSim], direct);
        Assert.Single(confirmed);
        Assert.Equal("sim:2", confirmed[0].Id);
    }

    [Fact]
    public void TrafficDeduplicatorAllowsUnmatchedWeakGeometry()
    {
        var deduplicator = new TrafficDeduplicator();
        var direct = new[] { Contact("tl:1", 60, 25, 25000, 300) };
        var distant = Contact("sim:1", 60.5, 25.5, 31000, 80);

        var remaining = deduplicator.SuppressDuplicates([distant], direct);

        Assert.Single(remaining);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void TrafficDeduplicatorTimeAlignsMovingTracksWithinOneSecond(double skewSeconds)
    {
        var deduplicator = new TrafficDeduplicator();
        var timestamp = DateTimeOffset.UtcNow;
        var direct = Contact("tl:1", 60, 25, 25000, 300) with { Timestamp = timestamp.AddSeconds(skewSeconds), GroundTrackDeg = 90 };
        var sim = Contact("sim:1", 60, 25, 25000, 300) with { Timestamp = timestamp, GroundTrackDeg = 90 };
        var directPosition0 = TacticalDisplay.Core.Math.GeoMath.DestinationPoint(60, 25, 90, 300 * 0.514444 * skewSeconds / 1852.0);
        direct = direct with { LatitudeDeg = directPosition0.latitudeDeg, LongitudeDeg = directPosition0.longitudeDeg };
        for (var sample = 0; sample < 4; sample++)
        {
            var sampleTime = timestamp.AddSeconds(sample * 2);
            var simPosition = TacticalDisplay.Core.Math.GeoMath.DestinationPoint(60, 25, 90, 300 * 0.514444 * sampleTime.Subtract(timestamp).TotalSeconds / 1852.0);
            var directPosition = TacticalDisplay.Core.Math.GeoMath.DestinationPoint(60, 25, 90, 300 * 0.514444 * (sampleTime.Subtract(timestamp).TotalSeconds + skewSeconds) / 1852.0);
            direct = direct with { Timestamp = sampleTime.AddSeconds(skewSeconds), LatitudeDeg = directPosition.latitudeDeg, LongitudeDeg = directPosition.longitudeDeg };
            sim = sim with { Timestamp = sampleTime, LatitudeDeg = simPosition.latitudeDeg, LongitudeDeg = simPosition.longitudeDeg };
            var remaining = deduplicator.SuppressDuplicates([sim], [direct]);
            if (sample < 2) Assert.Single(remaining);
            else Assert.Empty(remaining);
        }
    }

    [Fact]
    public void DirectCallsSignAndTankerCapabilityDefaultsToOff()
    {
        var profile = new StaticAircraftCapabilityResolver().Resolve("KC-135");
        Assert.True(profile.CanTanker);
        var tanker = new TacticalPeer("tl_random", "VIPER11", "KC135", new HashSet<string> { "aar.tanker" },
            new Dictionary<string, string> { ["tankerAvailability"] = "Off" }, Telemetry(60, 25), TimeSpan.Zero);
        Assert.NotEqual("AAR AVAILABLE", tanker.StatusText);
    }

    [Fact]
    public void InterestResolverHandlesHundredParticipantsAtTwentyHertzAcrossClusters()
    {
        var resolver = new PeerInterestResolver();
        var positions = Enumerable.Range(0, 100).ToDictionary(
            index => $"p{index}",
            index => index < 50
                ? Telemetry(40 + (index % 10) * 0.01, -74 + (index / 10) * 0.01)
                : Telemetry(60 + (index % 10) * 0.01, 25 + (index / 10) * 0.01));

        for (var tick = 0; tick < 20; tick++)
        {
            foreach (var id in positions.Keys)
            {
                var peers = resolver.Resolve(id, positions, 200);
                Assert.Equal(49, peers.Count);
            }
        }
    }

    private static TacticalTelemetry Telemetry(double latitude, double longitude) =>
        new(1, DateTimeOffset.UtcNow, latitude, longitude, 25000, 300, 300, 400);

    private static TrafficContactState Contact(string id, double latitude, double longitude, double altitude, double speed) =>
        new(id, null, latitude, longitude, altitude, 300, speed, DateTimeOffset.UtcNow);
}
