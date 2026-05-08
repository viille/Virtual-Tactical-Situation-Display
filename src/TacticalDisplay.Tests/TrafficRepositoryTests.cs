using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TrafficRepositoryTests
{
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
}
