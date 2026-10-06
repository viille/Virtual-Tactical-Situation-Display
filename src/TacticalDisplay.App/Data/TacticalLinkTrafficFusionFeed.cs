using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

/// <summary>Fuses direct TacticalLink tracks and suppresses their simulator duplicates before VATSIM matching runs.</summary>
public sealed class TacticalLinkTrafficFusionFeed : ITrafficDataFeed
{
    private readonly ITrafficDataFeed _inner;
    private readonly Func<IReadOnlyList<TacticalPeer>> _peerSource;
    private readonly TrafficFusion _fusion = new();

    public TacticalLinkTrafficFusionFeed(ITrafficDataFeed inner, Func<IReadOnlyList<TacticalPeer>> peerSource)
    {
        _inner = inner;
        _peerSource = peerSource;
        _inner.SnapshotReceived += OnSnapshotReceived;
        _inner.ConnectionChanged += OnConnectionChanged;
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public bool IsConnected => _inner.IsConnected;

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);
    public Task StopAsync() => _inner.StopAsync();

    private void OnConnectionChanged(object? sender, bool connected) => ConnectionChanged?.Invoke(this, connected);

    private void OnSnapshotReceived(object? sender, TrafficSnapshot snapshot)
    {
        var fused = _fusion.Fuse(snapshot, _peerSource());
        SnapshotReceived?.Invoke(this, snapshot with { Contacts = fused.Contacts });
    }

    public async ValueTask DisposeAsync()
    {
        _inner.SnapshotReceived -= OnSnapshotReceived;
        _inner.ConnectionChanged -= OnConnectionChanged;
        await _inner.DisposeAsync();
    }
}
