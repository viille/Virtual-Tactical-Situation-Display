using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class AarFuelAdapterFeed : ITrafficDataFeed, IAarPoseSource, IAarFuelAdapter
{
    private readonly ITrafficDataFeed _inner;
    private readonly IAarFuelAdapter _adapter;
    private CancellationTokenSource _bridgeMonitorCts = new();
    private Task? _bridgeMonitorTask;

    public AarFuelAdapterFeed(ITrafficDataFeed inner, IAarFuelAdapter adapter)
    {
        _inner = inner;
        _adapter = adapter;
        _inner.SnapshotReceived += OnSnapshotReceived;
        _inner.ConnectionChanged += OnConnectionChanged;
        _adapter.FuelSampled += OnFuelSampled;
        if (_inner is IAarPoseSource poseSource) poseSource.AarPoseSampled += OnAarPoseSampled;
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<TacticalDisplay.Core.Models.OwnshipState>? AarPoseSampled;
    public event EventHandler<AarFuelReading>? FuelSampled;
    public bool AarSamplingEnabled
    {
        get => _inner is IAarPoseSource poseSource && poseSource.AarSamplingEnabled;
        set { if (_inner is IAarPoseSource poseSource) poseSource.AarSamplingEnabled = value; }
    }
    public bool IsConnected => _inner.IsConnected;
    public bool IsAvailable => _adapter.IsAvailable;
    public bool CanReadFuel => _adapter.CanReadFuel;
    public bool CanWriteFuel => _adapter.CanWriteFuel;

    public AarFuelReading? ReadFuel() => _adapter.ReadFuel();
    public Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken) => _adapter.ApplyFuelDeltaKgAsync(deltaKg, cancellationToken);
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        if (_adapter is MsfsAarFuelAdapter msfsAdapter && _bridgeMonitorTask is null)
        {
            if (_bridgeMonitorCts.IsCancellationRequested)
            {
                _bridgeMonitorCts.Dispose();
                _bridgeMonitorCts = new CancellationTokenSource();
            }
            var monitorToken = _bridgeMonitorCts.Token;
            _bridgeMonitorTask = Task.Run(() => MonitorBridgeAsync(msfsAdapter, monitorToken), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        _bridgeMonitorCts.Cancel();
        if (_bridgeMonitorTask is not null)
        {
            try { await _bridgeMonitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _bridgeMonitorTask = null;
        }
        _bridgeMonitorCts.Dispose();
        _bridgeMonitorCts = new CancellationTokenSource();
        if (_adapter is MsfsAarFuelAdapter msfsAdapter) msfsAdapter.MarkSimulatorDisconnected();
        await _inner.StopAsync().ConfigureAwait(false);
    }

    private void OnSnapshotReceived(object? sender, TrafficSnapshot snapshot) => SnapshotReceived?.Invoke(this, snapshot);
    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected && _adapter is MsfsAarFuelAdapter msfsAdapter) msfsAdapter.MarkSimulatorDisconnected();
        ConnectionChanged?.Invoke(this, connected);
    }
    private void OnAarPoseSampled(object? sender, TacticalDisplay.Core.Models.OwnshipState sample) => AarPoseSampled?.Invoke(this, sample);
    private void OnFuelSampled(object? sender, AarFuelReading sample) => FuelSampled?.Invoke(this, sample);

    private async Task MonitorBridgeAsync(MsfsAarFuelAdapter adapter, CancellationToken cancellationToken)
    {
        var wasConnected = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_inner.IsConnected)
            {
                if (wasConnected) adapter.MarkSimulatorDisconnected();
                wasConnected = false;
                continue;
            }
            wasConnected = true;
            if (adapter.RuntimeState is AarBridgeRuntimeState.Error or AarBridgeRuntimeState.InstalledNotRunning or AarBridgeRuntimeState.NotInstalled)
                await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
            else if (adapter.RuntimeState is AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable)
            {
                try { await adapter.RefreshFuelStateAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { adapter.MarkSimulatorDisconnected(); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inner.SnapshotReceived -= OnSnapshotReceived;
        _inner.ConnectionChanged -= OnConnectionChanged;
        _adapter.FuelSampled -= OnFuelSampled;
        if (_inner is IAarPoseSource poseSource) poseSource.AarPoseSampled -= OnAarPoseSampled;
        _bridgeMonitorCts.Cancel();
        if (_bridgeMonitorTask is not null)
        {
            try { await _bridgeMonitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _bridgeMonitorCts.Dispose();
        await _inner.DisposeAsync();
    }
}
