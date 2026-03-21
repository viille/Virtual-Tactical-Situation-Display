using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public static class TrafficFeedFactory
{
    public static ITrafficDataFeed Create(TacticalDisplaySettings settings)
    {
        settings.DataSourceMode = DataSourceModes.Normalize(settings.DataSourceMode);

        if (DataSourceModes.IsMsfs(settings.DataSourceMode))
        {
            return new SimConnectTrafficFeed(settings);
        }

        if (DataSourceModes.IsXPlane(settings.DataSourceMode))
        {
            return new XPlaneTrafficFeed(settings);
        }

        return new DemoTrafficFeed();
    }
}
