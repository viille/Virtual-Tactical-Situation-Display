using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public interface ITrafficDataFeed : IAsyncDisposable
{
    event EventHandler<TrafficSnapshot>? SnapshotReceived;
    event EventHandler<bool>? ConnectionChanged;
    bool IsConnected { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
