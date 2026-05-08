using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public static class TrafficFeedFactory
{
    public static ITrafficDataFeed Create(TacticalDisplaySettings settings)
    {
        settings.DataSourceMode = DataSourceModes.Normalize(settings.DataSourceMode);

        ITrafficDataFeed feed;
        if (DataSourceModes.IsMsfs(settings.DataSourceMode))
        {
            feed = new SimConnectTrafficFeed(settings);
        }
        else if (DataSourceModes.IsXPlane12(settings.DataSourceMode))
        {
            feed = new XPlane12WebApiTrafficFeed(settings);
        }
        else if (DataSourceModes.IsXPlaneLegacy(settings.DataSourceMode))
        {
            feed = new XPlaneTrafficFeed(settings);
        }
        else
        {
            feed = new DemoTrafficFeed();
        }

        return settings.EnableVatsimCallsignLookup && DataSourceModes.UsesSimulatorConnection(settings.DataSourceMode)
            ? new VatsimCallsignTrafficFeed(feed, settings)
            : feed;
    }
}
