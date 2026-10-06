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
