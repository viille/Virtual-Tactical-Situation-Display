using System.IO;
using System.Diagnostics;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App.Data;

public sealed class MsfsAarFuelAdapter(IAarBridgeTransport transport, TimeProvider? timeProvider = null) : IAarFuelAdapter
{
    private const string LogSource = "MSFS-AAR";
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
        DataSourceDebugLog.Important(LogSource, $"===== MSFS AAR runtime session started | VTSD={ClientVersion} bridge=unknown protocol={SupportedProtocolVersion} =====");
        try
        {
            DataSourceDebugLog.Debug(LogSource, "CommBus transport available");
            DataSourceDebugLog.Debug(LogSource, "Sending HELLO");
            var hello = await SendAsync("HELLO", cancellationToken).ConfigureAwait(false);
            if (hello.ProtocolVersion != SupportedProtocolVersion)
            {
                lock (_gate) _bridgeVersion = hello.BridgeVersion;
                DataSourceDebugLog.Warn(LogSource, $"Protocol mismatch | desktop={SupportedProtocolVersion} bridge={hello.ProtocolVersion?.ToString() ?? "unknown"} bridgeVersion={hello.BridgeVersion ?? "unknown"}");
                SetRuntime(AarBridgeRuntimeState.ProtocolMismatch, $"Bridge protocol {hello.ProtocolVersion?.ToString() ?? "unknown"}; VTSD requires {SupportedProtocolVersion}.");
                return;
            }

            lock (_gate) _bridgeVersion = hello.BridgeVersion;
            DataSourceDebugLog.Debug(LogSource, $"HELLO accepted | protocol={hello.ProtocolVersion} bridgeVersion={hello.BridgeVersion ?? "unknown"}");
            DataSourceDebugLog.Important(LogSource, $"===== MSFS AAR bridge connected | VTSD={ClientVersion} bridge={hello.BridgeVersion ?? "unknown"} protocol={hello.ProtocolVersion} =====");
            DataSourceDebugLog.Debug(LogSource, "Requesting bridge capabilities");
            var capabilities = await SendAsync("GET_CAPABILITIES", cancellationToken).ConfigureAwait(false);
            if (DataSourceDebugLog.IsEnabled && capabilities.Diagnostics is { } diagnostics)
            {
                DataSourceDebugLog.Debug(LogSource, $"Write probe reason | reason={diagnostics.FuelReadOnlyReason ?? "none"}");
                foreach (var probe in diagnostics.WriteProbe) DataSourceDebugLog.Debug(LogSource, "Write probe | " + probe);
            }
            lock (_gate)
            {
                _canRead = capabilities.Capabilities.Contains("fuel.read", StringComparer.Ordinal);
                _canWrite = capabilities.Capabilities.Contains("fuel.write", StringComparer.Ordinal);
            }
            if (_canRead) await RefreshFuelStateAsync(cancellationToken).ConfigureAwait(false);
            var canWrite = _canRead && _canWrite && _fuelState is { Tanks.Length: > 0 } fuelState && fuelState.Tanks.All(tank => tank.Writable);
            DataSourceDebugLog.Debug(LogSource, $"Capabilities | fuelRead={_canRead} fuelWrite={_canWrite}");
            if (!canWrite)
            {
                var reason = !_canRead ? "protocol capability did not advertise fuel.read" : !_canWrite ? "protocol capability did not advertise fuel.write" :
                    _fuelState is not { Tanks.Length: > 0 } ? "no tanks discovered" : "one or more discovered tanks failed the write probe";
                DataSourceDebugLog.Warn(LogSource, $"Fuel write unavailable | reason={reason}");
            }
            SetRuntime(canWrite ? AarBridgeRuntimeState.ConnectedWritable : AarBridgeRuntimeState.ConnectedReadOnly,
                !_canRead ? "The bridge is connected, but this aircraft does not expose readable fuel state. Dry Hookup is available." :
                !canWrite ? "This aircraft does not expose a verified writable fuel system. Dry Hookup is available." : null);
            DataSourceDebugLog.Debug(LogSource, $"Runtime readiness | bridgeVersion={BridgeVersion ?? "unknown"} protocol={SupportedProtocolVersion} fuelRead={_canRead} fuelWrite={canWrite} tanks={_fuelState?.Tanks.Length ?? 0} currentKg={_fuelState?.CurrentFuelKg:0.00} capacityKg={_fuelState?.CapacityKg:0.00} reason={Diagnostic ?? "none"}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DataSourceDebugLog.Error(LogSource, "Bridge initialization failed", ex);
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
        LogFuelState(response.FuelState, logTanks: true);
        if (response.Diagnostics?.FuelReadOnlyReason is { Length: > 0 } reason)
        {
            DataSourceDebugLog.Warn(LogSource, $"Fuel capability diagnostic | reason={reason}");
            SetRuntime(AarBridgeRuntimeState.ConnectedReadOnly, reason);
        }
    }

    public async Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(deltaKg) || deltaKg == 0)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel delta must be finite and non-zero.");
        if (!CanWriteFuel)
        {
            DataSourceDebugLog.Warn(LogSource, $"Fuel mutation rejected | requestedKg={deltaKg:0.00} reason=bridge not writable");
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "MSFS AAR Bridge is not connected with writable fuel capability.");
        }

        AarBridgeFuelState? before;
        lock (_gate) before = _fuelState;
        if (before is null || _clock.GetUtcNow() - before.SampledAtUtc > FuelFreshness)
        {
            DataSourceDebugLog.Warn(LogSource, $"Fuel mutation rejected | requestedKg={deltaKg:0.00} reason=fuel state stale");
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel state is stale.");
        }

        try
        {
            DataSourceDebugLog.Debug(LogSource, $"Fuel mutation started | requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} tanks={before.Tanks.Length}");
            var response = await SendAsync("APPLY_FUEL_DELTA", cancellationToken, deltaKg, allowFailedResponse: true).ConfigureAwait(false);
            if (response.FuelState is null || !IsValid(response.FuelState))
            {
                DataSourceDebugLog.Error(LogSource, $"Fuel mutation failed | requestId={response.RequestId} requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} afterKg=unknown appliedKg={response.AppliedKg?.ToString("0.00") ?? "unknown"} status={response.Status ?? "unknown"} error={response.Error ?? "bridge omitted valid read-back"}");
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, response.Error ?? "Bridge omitted the mandatory post-write fuel readback.");
            }

            if (response.AppliedKg is not { } actual || !double.IsFinite(actual))
            {
                DataSourceDebugLog.Error(LogSource, $"Fuel mutation failed | requestId={response.RequestId} requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} afterKg={response.FuelState.CurrentFuelKg:0.00} appliedKg=unknown status={response.Status ?? "unknown"} error=bridge omitted applied mass");
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Bridge omitted the actual applied mass from its read-after-write result.");
            }

            UpdateFuelState(response.FuelState);
            if (!double.IsFinite(actual) || Math.Abs(actual) > Math.Abs(deltaKg) + 0.05 || actual != 0 && Math.Sign(actual) != Math.Sign(deltaKg))
            {
                DataSourceDebugLog.Error(LogSource, $"Fuel mutation failed | requestId={response.RequestId} requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} afterKg={response.FuelState.CurrentFuelKg:0.00} appliedKg={actual:0.00} status=invalid-readback error=direction-or-bound-mismatch");
                return new AarFuelApplyResult(deltaKg, actual, AarFuelApplyStatus.Failed, "Simulator readback did not match the requested fuel direction or bound.");
            }

            var status = string.Equals(response.Status, "Failed", StringComparison.OrdinalIgnoreCase) || Math.Abs(actual) < 0.001
                ? AarFuelApplyStatus.Failed
                : string.Equals(response.Status, "Partial", StringComparison.OrdinalIgnoreCase) || Math.Abs(actual - deltaKg) > 0.05
                    ? AarFuelApplyStatus.Partial
                    : AarFuelApplyStatus.Success;
            var mutationMessage = $"Fuel mutation | requestId={response.RequestId} requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} afterKg={response.FuelState.CurrentFuelKg:0.00} appliedKg={actual:0.00} status={status} error={response.Error ?? "none"}";
            if (status == AarFuelApplyStatus.Success) DataSourceDebugLog.Debug(LogSource, mutationMessage);
            else DataSourceDebugLog.Warn(LogSource, mutationMessage);
            LogFuelState(response.FuelState, logTanks: true, response.RequestId);
            if (DataSourceDebugLog.IsEnabled)
            {
                IReadOnlyDictionary<string, double> targetDeltas;
                try { targetDeltas = AarBridgeFuelDistributionPolicy.Distribute(before.Tanks, deltaKg).ToDictionary(item => item.TankId, item => item.DeltaKg, StringComparer.Ordinal); }
                catch { targetDeltas = new Dictionary<string, double>(StringComparer.Ordinal); }
                var previousTanks = before.Tanks.ToDictionary(tank => tank.TankId, StringComparer.Ordinal);
                foreach (var tank in response.FuelState.Tanks)
                {
                    var beforeKg = previousTanks.TryGetValue(tank.TankId, out var oldTank) ? oldTank.CurrentKg : 0;
                    var targetKg = beforeKg + targetDeltas.GetValueOrDefault(tank.TankId);
                    DataSourceDebugLog.Debug(LogSource, $"Fuel mutation tank | requestId={response.RequestId} tank={tank.TankId} beforeKg={beforeKg:0.00} targetKg={targetKg:0.00} readBackKg={tank.CurrentKg:0.00} appliedKg={tank.CurrentKg - beforeKg:0.00}");
                }
            }
            return new AarFuelApplyResult(deltaKg, actual, status, response.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DataSourceDebugLog.Error(LogSource, $"Fuel mutation failed | requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} afterKg=unknown appliedKg=unknown", ex);
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, ex.Message);
        }
    }

    public void MarkNotInstalled() => SetRuntime(AarBridgeRuntimeState.NotInstalled, "MSFS AAR Bridge is required for live fuel transfer. Dry Hookup remains available without the bridge.");

    public void MarkSimulatorDisconnected() => SetRuntime(AarBridgeRuntimeState.Error, "Microsoft Flight Simulator is disconnected; live fuel transfer is stopped.");

    public void MarkInstalledNotRunning(string? version)
    {
        lock (_gate) _bridgeVersion = version;
        SetRuntime(AarBridgeRuntimeState.InstalledNotRunning, "Bridge installed. Restart Microsoft Flight Simulator to activate it.");
    }

    private async Task<AarBridgeResponse> SendAsync(string action, CancellationToken cancellationToken, double? deltaKg = null, bool allowFailedResponse = false)
    {
        if (!transport.IsConnected) throw new IOException("Microsoft Flight Simulator is not connected.");
        var request = new AarBridgeRequest(Guid.NewGuid().ToString("N"), action, SupportedProtocolVersion, ClientVersion, deltaKg);
        var timer = Stopwatch.StartNew();
        DataSourceDebugLog.Debug(LogSource, $"Request | action={action} requestId={request.RequestId}{(deltaKg.HasValue ? $" requestedKg={deltaKg.Value:0.00}" : string.Empty)}");
        AarBridgeResponse response;
        try { response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            DataSourceDebugLog.Warn(LogSource, $"Bridge request timeout | action={action} requestId={request.RequestId} timeoutMs={timer.ElapsedMilliseconds} simConnectConnected={transport.IsConnected}");
            throw;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Error(LogSource, $"Request failed | action={action} requestId={request.RequestId} durationMs={timer.ElapsedMilliseconds}", ex);
            throw;
        }
        DataSourceDebugLog.Debug(LogSource, $"Response | action={action} requestId={request.RequestId} status={response.Status ?? "unknown"} durationMs={timer.ElapsedMilliseconds} error={response.Error ?? "none"}");
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

    private static void LogFuelState(AarBridgeFuelState state, bool logTanks, string? requestId = null)
    {
        DataSourceDebugLog.ThrottledDebug(LogSource, "fuel-summary", TimeSpan.FromSeconds(5), () =>
            $"Fuel system | tanks={state.Tanks.Length} currentKg={state.CurrentFuelKg:0.00} capacityKg={state.CapacityKg:0.00} fuelWeightPerGallonLb={state.FuelWeightPerGallonLb:0.00} writable={state.Tanks.All(tank => tank.Writable)} sampledAt={state.SampledAtUtc:O}");
        if (!logTanks || !DataSourceDebugLog.IsEnabled) return;
        foreach (var tank in state.Tanks)
            DataSourceDebugLog.Debug(LogSource, $"Fuel mutation tank | requestId={requestId ?? "discovery"} id={tank.TankId} currentKg={tank.CurrentKg:0.00} capacityKg={tank.CapacityKg:0.00} writable={tank.Writable}");
    }

    private void SetRuntime(AarBridgeRuntimeState state, string? diagnostic)
    {
        bool changed;
        AarBridgeRuntimeState previous;
        lock (_gate)
        {
            previous = _runtimeState;
            changed = _runtimeState != state;
            _runtimeState = state;
            _diagnostic = diagnostic;
            if (state is AarBridgeRuntimeState.NotInstalled or AarBridgeRuntimeState.InstalledNotRunning or AarBridgeRuntimeState.Error)
            {
                _canRead = false;
                _canWrite = false;
            }
        }
        if (changed)
        {
            var message = $"Runtime state | from={previous} to={state} bridgeVersion={BridgeVersion ?? "unknown"} diagnostic={diagnostic ?? "none"}";
            if (previous == AarBridgeRuntimeState.ConnectedWritable && state != AarBridgeRuntimeState.ConnectedWritable)
                DataSourceDebugLog.Warn(LogSource, "Writable fuel capability lost | " + message);
            else DataSourceDebugLog.Debug(LogSource, message);
            if (state is AarBridgeRuntimeState.NotInstalled or AarBridgeRuntimeState.InstalledNotRunning or AarBridgeRuntimeState.Error)
                DataSourceDebugLog.Important(LogSource, "===== MSFS AAR runtime session ended =====");
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
