using System.IO;

namespace TacticalDisplay.App.Data;

public sealed class MsfsAarFuelAdapter(IAarBridgeTransport transport, TimeProvider? timeProvider = null) : IAarFuelAdapter
{
    public const int SupportedProtocolVersion = 1;
    private const string ClientVersion = "0.16.0";
    private static readonly TimeSpan FuelFreshness = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private AarBridgeRuntimeState _runtimeState = AarBridgeRuntimeState.InstalledNotRunning;
    private AarBridgeFuelState? _fuelState;
    private string? _bridgeVersion;
    private string? _diagnostic;
    private bool _canRead;
    private bool _canWrite;

    public event EventHandler<AarFuelReading>? FuelSampled;
    public event EventHandler<AarBridgeRuntimeState>? RuntimeStateChanged;
    public AarBridgeRuntimeState RuntimeState { get { lock (_gate) return _runtimeState; } }
    public string? BridgeVersion { get { lock (_gate) return _bridgeVersion; } }
    public string? Diagnostic { get { lock (_gate) return _diagnostic; } }
    public bool IsAvailable => transport.IsConnected && RuntimeState is AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable;
    public bool CanReadFuel => IsAvailable && _canRead && ReadFuel() is not null;
    public bool CanWriteFuel => IsAvailable && RuntimeState == AarBridgeRuntimeState.ConnectedWritable && _canWrite &&
        CanReadFuel && _fuelState is { Tanks.Length: > 0 } fuel && fuel.Tanks.All(tank => tank.Writable);

    public AarFuelReading? ReadFuel()
    {
        lock (_gate)
        {
            if (_fuelState is not { } fuel || !IsValid(fuel) || _clock.GetUtcNow() - fuel.SampledAtUtc > FuelFreshness || fuel.SampledAtUtc - _clock.GetUtcNow() > TimeSpan.FromSeconds(1)) return null;
            return new AarFuelReading(fuel.CurrentFuelKg, fuel.CapacityKg, fuel.SampledAtUtc);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _canRead = false;
            _canWrite = false;
            _fuelState = null;
        }
        SetRuntime(AarBridgeRuntimeState.Connecting, null);
        try
        {
            var hello = await SendAsync("HELLO", cancellationToken).ConfigureAwait(false);
            if (hello.ProtocolVersion != SupportedProtocolVersion)
            {
                lock (_gate) _bridgeVersion = hello.BridgeVersion;
                SetRuntime(AarBridgeRuntimeState.ProtocolMismatch, $"Bridge protocol {hello.ProtocolVersion?.ToString() ?? "unknown"}; VTSD requires {SupportedProtocolVersion}.");
                return;
            }

            lock (_gate) _bridgeVersion = hello.BridgeVersion;
            var capabilities = await SendAsync("GET_CAPABILITIES", cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _canRead = capabilities.Capabilities.Contains("fuel.read", StringComparer.Ordinal);
                _canWrite = capabilities.Capabilities.Contains("fuel.write", StringComparer.Ordinal);
            }
            if (_canRead) await RefreshFuelStateAsync(cancellationToken).ConfigureAwait(false);
            var canWrite = _canRead && _canWrite && _fuelState is { Tanks.Length: > 0 } fuelState && fuelState.Tanks.All(tank => tank.Writable);
            SetRuntime(canWrite ? AarBridgeRuntimeState.ConnectedWritable : AarBridgeRuntimeState.ConnectedReadOnly,
                !_canRead ? "The bridge is connected, but this aircraft does not expose readable fuel state. Dry Hookup is available." :
                !canWrite ? "This aircraft does not expose a verified writable fuel system. Dry Hookup is available." : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SetRuntime(AarBridgeRuntimeState.Error, ex.Message);
        }
    }

    public async Task RefreshFuelStateAsync(CancellationToken cancellationToken)
    {
        if (!transport.IsConnected || !_canRead) return;
        var response = await SendAsync("GET_FUEL_STATE", cancellationToken).ConfigureAwait(false);
        if (response.FuelState is null || !IsValid(response.FuelState))
            throw new InvalidDataException(response.Error ?? "The bridge returned invalid fuel state.");
        UpdateFuelState(response.FuelState);
    }

    public async Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(deltaKg) || deltaKg == 0)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel delta must be finite and non-zero.");
        if (!CanWriteFuel)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "MSFS AAR Bridge is not connected with writable fuel capability.");

        AarBridgeFuelState? before;
        lock (_gate) before = _fuelState;
        if (before is null || _clock.GetUtcNow() - before.SampledAtUtc > FuelFreshness)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel state is stale.");

        try
        {
            var response = await SendAsync("APPLY_FUEL_DELTA", cancellationToken, deltaKg, allowFailedResponse: true).ConfigureAwait(false);
            if (response.FuelState is null || !IsValid(response.FuelState))
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, response.Error ?? "Bridge omitted the mandatory post-write fuel readback.");

            var actual = response.FuelState.CurrentFuelKg - before.CurrentFuelKg;
            UpdateFuelState(response.FuelState);
            if (!double.IsFinite(actual) || Math.Abs(actual) > Math.Abs(deltaKg) + 0.05 || actual != 0 && Math.Sign(actual) != Math.Sign(deltaKg))
                return new AarFuelApplyResult(deltaKg, actual, AarFuelApplyStatus.Failed, "Simulator readback did not match the requested fuel direction or bound.");

            var status = string.Equals(response.Status, "Failed", StringComparison.OrdinalIgnoreCase) || Math.Abs(actual) < 0.001
                ? AarFuelApplyStatus.Failed
                : string.Equals(response.Status, "Partial", StringComparison.OrdinalIgnoreCase) || Math.Abs(actual - deltaKg) > 0.05
                    ? AarFuelApplyStatus.Partial
                    : AarFuelApplyStatus.Success;
            return new AarFuelApplyResult(deltaKg, actual, status, response.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, ex.Message);
        }
    }

    public void MarkNotInstalled() => SetRuntime(AarBridgeRuntimeState.NotInstalled, "MSFS AAR Bridge is required for live fuel transfer. Dry Hookup remains available without the bridge.");

    public void MarkInstalledNotRunning(string? version) 
    {
        lock (_gate) _bridgeVersion = version;
        SetRuntime(AarBridgeRuntimeState.InstalledNotRunning, "Bridge installed. Restart Microsoft Flight Simulator to activate it.");
    }

    private async Task<AarBridgeResponse> SendAsync(string action, CancellationToken cancellationToken, double? deltaKg = null, bool allowFailedResponse = false)
    {
        if (!transport.IsConnected) throw new IOException("Microsoft Flight Simulator is not connected.");
        var request = new AarBridgeRequest(Guid.NewGuid().ToString("N"), action, SupportedProtocolVersion, ClientVersion, deltaKg);
        var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            throw new InvalidDataException("Bridge response request ID did not match.");
        if (!string.Equals(response.Action, action, StringComparison.Ordinal))
            throw new InvalidDataException("Bridge response action did not match the request.");
        if (!string.Equals(action, "HELLO", StringComparison.Ordinal) && response.ProtocolVersion is { } protocol && protocol != SupportedProtocolVersion)
            throw new InvalidDataException($"Bridge protocol {protocol} does not match {SupportedProtocolVersion}.");
        if (!allowFailedResponse && string.Equals(response.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(response.Error ?? $"Bridge operation {action} failed.");
        return response;
    }

    private void UpdateFuelState(AarBridgeFuelState state)
    {
        lock (_gate) _fuelState = state;
        FuelSampled?.Invoke(this, new AarFuelReading(state.CurrentFuelKg, state.CapacityKg, state.SampledAtUtc));
    }

    private void SetRuntime(AarBridgeRuntimeState state, string? diagnostic)
    {
        bool changed;
        lock (_gate)
        {
            changed = _runtimeState != state;
            _runtimeState = state;
            _diagnostic = diagnostic;
            if (state is AarBridgeRuntimeState.NotInstalled or AarBridgeRuntimeState.InstalledNotRunning or AarBridgeRuntimeState.Error)
            {
                _canRead = false;
                _canWrite = false;
            }
        }
        if (changed) RuntimeStateChanged?.Invoke(this, state);
    }

    private static bool IsValid(AarBridgeFuelState state) =>
        double.IsFinite(state.CurrentFuelKg) && double.IsFinite(state.CapacityKg) &&
        state.CurrentFuelKg >= 0 && state.CapacityKg > 0 && state.CurrentFuelKg <= state.CapacityKg + 0.05 &&
        state.Tanks.Length > 0 && state.Tanks.All(tank => !string.IsNullOrWhiteSpace(tank.TankId) &&
            double.IsFinite(tank.CurrentKg) && double.IsFinite(tank.CapacityKg) &&
            tank.CurrentKg >= 0 && tank.CapacityKg >= 0 && tank.CurrentKg <= tank.CapacityKg + 0.05);
}
