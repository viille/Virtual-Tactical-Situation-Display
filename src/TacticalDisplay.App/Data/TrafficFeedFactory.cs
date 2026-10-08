using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.Cloud;
using Microsoft.Extensions.DependencyInjection;

namespace TacticalDisplay.App.Data;

public static class TrafficFeedFactory
{
    public static ITrafficDataFeed Create(TacticalDisplaySettings settings, Func<IReadOnlyList<TacticalPeer>>? peerSource = null)
    {
        settings.DataSourceMode = DataSourceModes.Normalize(settings.DataSourceMode);

        ITrafficDataFeed feed;
        if (DataSourceModes.IsMsfs(settings.DataSourceMode))
        {
            var simConnectFeed = new SimConnectTrafficFeed(settings);
            feed = new AarFuelAdapterFeed(simConnectFeed, new MsfsAarFuelAdapter(simConnectFeed));
        }
        else if (DataSourceModes.IsXPlane12(settings.DataSourceMode))
        {
            var xplane12Feed = new XPlane12WebApiTrafficFeed(settings);
            var fuelAdapter = new XPlane12AarFuelAdapter(xplane12Feed);
            xplane12Feed.AttachAarFuelAdapter(fuelAdapter);
            feed = new AarFuelAdapterFeed(xplane12Feed, fuelAdapter);
        }
        else if (DataSourceModes.IsXPlaneLegacy(settings.DataSourceMode))
        {
            feed = new XPlaneTrafficFeed(settings);
        }
        else
        {
            feed = new DemoTrafficFeed();
        }

        feed = new TacticalLinkTrafficFusionFeed(feed, peerSource ?? (() => []));
        if (settings.EnableVatsimCallsignLookup && DataSourceModes.UsesSimulatorConnection(settings.DataSourceMode))
        {
            DataSourceDebugLog.Info("VATSIM", $"Wrapping {settings.DataSourceMode} feed with VATSIM callsign lookup");
            return new VatsimCallsignTrafficFeed(feed, settings, () =>
            {
                var user = CloudBootstrapper.Provider.GetRequiredService<AuthService>().State.User;
                return new VatsimOwnshipIdentity(user?.VatsimCid, settings.OwnCallsign);
            });
        }

        DataSourceDebugLog.Info("VATSIM", $"VATSIM callsign lookup disabled or not applicable | source={settings.DataSourceMode} enabled={settings.EnableVatsimCallsignLookup}");
        return feed;
    }
}
