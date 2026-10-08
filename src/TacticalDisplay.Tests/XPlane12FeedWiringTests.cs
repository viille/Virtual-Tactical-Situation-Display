using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class XPlane12FeedWiringTests
{
    [Fact]
    public async Task FactoryPreservesPoseAndFuelAdapterThroughWrappers()
    {
        var settings = new TacticalDisplaySettings
        {
            DataSourceMode = DataSourceModes.XPlane12,
            EnableVatsimCallsignLookup = false
        };
        var feed = TrafficFeedFactory.Create(settings);

        try
        {
            var pose = Assert.IsAssignableFrom<IAarPoseSource>(feed);
            var fuel = Assert.IsAssignableFrom<IAarFuelAdapter>(feed);
            pose.AarSamplingEnabled = true;

            Assert.True(pose.AarSamplingEnabled);
            Assert.False(fuel.CanWriteFuel);
        }
        finally
        {
            await feed.DisposeAsync();
        }
    }
}
