using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TacticalComputationEngineTests
{
    [Fact]
    public void Compute_FiltersTargetsBelowMinimumTrackedAltitude()
    {
        var engine = new TacticalComputationEngine();
        var settings = new TacticalDisplaySettings
        {
            MinTrackedAltitudeFt = 1000
        };
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, DateTimeOffset.UtcNow);
        var contact = new TrafficContactState("T1", null, 60.1, 24.0, 900, 180, 250, DateTimeOffset.UtcNow);
        var tracked = new TrafficRepository.TrackedContact(contact, TargetCategory.Unknown);

        var computed = engine.Compute(null, ownship, tracked, settings);

        Assert.Null(computed);
    }

    [Fact]
    public void Compute_UsesVelocityProjectionForClosure()
    {
        var engine = new TacticalComputationEngine();
        var settings = new TacticalDisplaySettings();
        var now = DateTimeOffset.UtcNow;
        var ownship = new OwnshipState("OWN", 60.0, 24.0, 5000, 0, 300, now);
        var contact = new TrafficContactState("T1", null, 60.1, 24.0, 5000, 180, 250, now);
        var tracked = new TrafficRepository.TrackedContact(contact, TargetCategory.Unknown);

        var computed = engine.Compute(null, ownship, tracked, settings);

        Assert.NotNull(computed);
        Assert.NotNull(computed!.ClosureKt);
        Assert.InRange(computed.ClosureKt!.Value, 549.9, 550.1);
    }
}
