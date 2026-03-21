using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public static class TrafficFeedFactory
{
    public static ITrafficDataFeed Create(TacticalDisplaySettings settings)
    {
        if (string.Equals(settings.DataSourceMode, "SimConnect", StringComparison.OrdinalIgnoreCase))
        {
            return new SimConnectTrafficFeed(settings);
        }

        return new DemoTrafficFeed();
    }
}
