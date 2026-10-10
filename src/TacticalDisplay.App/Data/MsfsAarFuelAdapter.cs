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
    private readonly SemaphoreSlim _fuelMutationGate = new(1, 1);
    private AarBridgeRuntimeState _runtimeState = AarBridgeRuntimeState.InstalledNotRunning;
    private AarBridgeFuelState? _fuelState;
    private string? _bridgeVersion;
    private string? _diagnostic;
    private bool _canRead;
    private bool _canWrite;
    private string[] _writeMethods = [];
    private string? _preferredWriteMethod;
    private string? _writeProfileSignature;

    public event EventHandler<AarFuelReading>? FuelSampled;
    public event EventHandler<AarBridgeRuntimeState>? RuntimeStateChanged;
    public AarBridgeRuntimeState RuntimeState { get { lock (_gate) return _runtimeState; } }
    public string? BridgeVersion { get { lock (_gate) return _bridgeVersion; } }
    public string? Diagnostic { get { lock (_gate) return _diagnostic; } }
    public bool IsAvailable => transport.IsConnected && RuntimeState is AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable;
    public bool CanReadFuel => IsAvailable && _canRead && ReadFuel() is not null;
    public bool CanWriteFuel => IsAvailable && RuntimeState == AarBridgeRuntimeState.ConnectedWritable && _canWrite &&
        CanReadFuel && _fuelState is { Tanks.Length: > 0 };

    public AarFuelReading? ReadFuel()
    {
        lock (_gate)
        {
            if (_fuelState is not { } fuel || !IsValid(fuel) || fuel.SampledAtUtc == default ||
                _clock.GetUtcNow() - fuel.SampledAtUtc > FuelFreshness || fuel.SampledAtUtc - _clock.GetUtcNow() > TimeSpan.FromSeconds(1)) return null;
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
            _writeMethods = [];
            _preferredWriteMethod = null;
            _writeProfileSignature = null;
        }
        SetRuntime(AarBridgeRuntimeState.Connecting, null);
        DataSourceDebugLog.Info(LogSource, $"MSFS AAR bridge connection attempt started | VTSD={ClientVersion} protocol={SupportedProtocolVersion}");
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
                _writeMethods = capabilities.Diagnostics?.WriteMethods ?? [];
            }
            var fuelReadDiagnostic = capabilities.Diagnostics?.FuelReadDiagnostic;
            var fuelReadOnlyReason = capabilities.Diagnostics?.FuelReadOnlyReason;
            if (!_canRead)
            {
                var detail = !string.IsNullOrWhiteSpace(fuelReadDiagnostic) ? fuelReadDiagnostic : fuelReadOnlyReason ?? "the bridge did not report a reason";
                DataSourceDebugLog.Important(LogSource, $"Aircraft fuel read unavailable | diagnostic={detail}");
            }
            if (_canRead) await RefreshFuelStateAsync(cancellationToken).ConfigureAwait(false);
            // GET_CAPABILITIES performs the bridge's same-value setter/read-back probe. Each
            // APPLY_FUEL_DELTA request probes again before writing, so a normal state snapshot
            // must not independently revoke that advertised capability based on per-tank flags.
            var canWrite = _canRead && _canWrite && _fuelState is { Tanks.Length: > 0 };
            DataSourceDebugLog.Debug(LogSource, $"Capabilities | fuelRead={_canRead} fuelWrite={_canWrite}");
            if (!canWrite)
            {
                var reason = !_canRead ? "protocol capability did not advertise fuel.read" : !_canWrite ? "protocol capability did not advertise fuel.write" :
                    "no valid fuel tank state is available";
                DataSourceDebugLog.Warn(LogSource, $"Fuel write unavailable | reason={reason}");
            }
            SetRuntime(canWrite ? AarBridgeRuntimeState.ConnectedWritable : AarBridgeRuntimeState.ConnectedReadOnly,
                !_canRead ? $"Fuel read unavailable: {fuelReadDiagnostic ?? fuelReadOnlyReason ?? "the bridge did not report a reason"}. Dry Hookup is available." :
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
        UpdateWriteMethods(response.Diagnostics);
        UpdateFuelState(response.FuelState);
        LogFuelState(response.FuelState, logTanks: true);
        if (response.Diagnostics is { WriteMethods.Length: 0, FuelReadOnlyReason.Length: > 0 } diagnostics &&
            response.FuelState.Tanks.Any(tank => !tank.Writable))
        {
            var reason = diagnostics.FuelReadOnlyReason!;
            DataSourceDebugLog.Warn(LogSource, $"Fuel write capability revoked by tank state | reason={reason}");
            SetRuntime(AarBridgeRuntimeState.ConnectedReadOnly, reason);
        }
    }

    public async Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
    {
        await _fuelMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ApplyFuelDeltaCoreAsync(deltaKg, cancellationToken).ConfigureAwait(false); }
        finally { _fuelMutationGate.Release(); }
    }

    private async Task<AarFuelApplyResult> ApplyFuelDeltaCoreAsync(double deltaKg, CancellationToken cancellationToken)
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
        if (before is null || ReadFuel() is null)
        {
            DataSourceDebugLog.Warn(LogSource, $"Fuel mutation rejected | requestedKg={deltaKg:0.00} reason=fuel state stale");
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel state is stale.");
        }

        try
        {
            var liveCapabilities = await SendAsync("GET_FUEL_STATE", cancellationToken).ConfigureAwait(false);
            UpdateWriteMethods(liveCapabilities.Diagnostics);
            if (liveCapabilities.FuelState is { } liveState && IsValid(liveState))
            {
                before = liveState;
                UpdateFuelState(liveState);
            }
            DataSourceDebugLog.Debug(LogSource, $"Fuel mutation started | requestedKg={deltaKg:0.00} beforeKg={before.CurrentFuelKg:0.00} tanks={before.Tanks.Length}");
            var profileSignature = string.Join("|", before.Tanks.Select(tank => $"{tank.TankId}:{tank.CapacityKg:0.0}"));
            string? preferredMethod;
            lock (_gate)
            {
                if (!string.Equals(_writeProfileSignature, profileSignature, StringComparison.Ordinal))
                {
                    _preferredWriteMethod = null;
                    _writeProfileSignature = profileSignature;
                }
                preferredMethod = _preferredWriteMethod;
            }
            var configuredMethods = _writeMethods.Length == 0 ? new[] { "auto" } : _writeMethods;
            IReadOnlyList<string> methods = preferredMethod is null ? configuredMethods :
                new[] { preferredMethod }.Concat(configuredMethods.Where(method => method != preferredMethod)).ToArray();
            var lastError = "No supported write method produced a persistent fuel change.";
            foreach (var method in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baselineResponse = await SendAsync("GET_FUEL_STATE", cancellationToken).ConfigureAwait(false);
                UpdateWriteMethods(baselineResponse.Diagnostics);
                var attemptBefore = baselineResponse.FuelState is { } baseline && IsValid(baseline) ? baseline : before;
                UpdateFuelState(attemptBefore);
                DataSourceDebugLog.Important(LogSource, $"Fuel write strategy | method={method} requestedKg={deltaKg:0.00} beforeKg={attemptBefore.CurrentFuelKg:0.00}");
                var movementToleranceKg = Math.Max(0.01, Math.Min(0.25, Math.Abs(deltaKg) * 0.001));
                var requestToleranceKg = Math.Max(0.01, Math.Min(0.25, Math.Abs(deltaKg) * 0.001));
                var stableToleranceKg = Math.Max(0.05, Math.Min(3, Math.Abs(deltaKg) * 0.01));
                AarBridgeResponse response;
                var requestError = (string?)null;
                try
                {
                    response = await SendAsync("APPLY_FUEL_DELTA", cancellationToken, deltaKg,
                        allowFailedResponse: true, writeMethod: method).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    requestError = $"{method}: {ex.Message}";
                    lastError = requestError;
                    DataSourceDebugLog.Warn(LogSource, $"Fuel write strategy request failed | method={method} error={ex.Message}");
                    response = new AarBridgeResponse();
                }

                if (response.FuelState is { } immediate && IsValid(immediate)) UpdateFuelState(immediate);
                else if (string.Equals(response.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    lastError = $"{method}: {response.Error ?? "bridge rejected the strategy"}";

                AarBridgeFuelState? previousSample = null;
                AarBridgeFuelState? verifiedState = null;
                AarBridgeFuelState? lastObservedState = response.FuelState is { } responseState && IsValid(responseState)
                    ? responseState
                    : null;
                var possibleMutationObserved = lastObservedState is not null &&
                    Math.Abs(lastObservedState.CurrentFuelKg - attemptBefore.CurrentFuelKg) >= movementToleranceKg;
                var isAircraftNativeMethod = string.Equals(method, "a32nx-native", StringComparison.Ordinal);
                var sampleCount = isAircraftNativeMethod ? 30 : method == preferredMethod ? 2 : 3;
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    await Task.Delay(isAircraftNativeMethod
                        ? TimeSpan.FromMilliseconds(500)
                        : sample == 0 ? TimeSpan.FromMilliseconds(450) : TimeSpan.FromMilliseconds(650), _clock, cancellationToken).ConfigureAwait(false);
                    var stateResponse = await SendAsync("GET_FUEL_STATE", cancellationToken).ConfigureAwait(false);
                    UpdateWriteMethods(stateResponse.Diagnostics);
                    if (stateResponse.FuelState is not { } current || !IsValid(current)) continue;
                    UpdateFuelState(current);
                    var applied = current.CurrentFuelKg - attemptBefore.CurrentFuelKg;
                    possibleMutationObserved |= Math.Abs(applied) >= movementToleranceKg;
                    var directionMatches = Math.Abs(applied) >= movementToleranceKg && Math.Sign(applied) == Math.Sign(deltaKg);
                    var withinRequest = Math.Abs(applied) <= Math.Abs(deltaKg) + requestToleranceKg;
                    var stable = previousSample is not null && Math.Abs(current.CurrentFuelKg - previousSample.CurrentFuelKg) <= stableToleranceKg;
                    DataSourceDebugLog.Debug(LogSource, $"Fuel write verification | method={method} sample={sample + 1} beforeKg={attemptBefore.CurrentFuelKg:0.00} readKg={current.CurrentFuelKg:0.00} appliedKg={applied:0.00} direction={directionMatches} stable={stable}");
                    previousSample = current;
                    lastObservedState = current;
                    verifiedState = directionMatches && withinRequest && stable ? current : null;
                    if (verifiedState is not null) break;
                }

                if (isAircraftNativeMethod) await FinishFuelOperationAsync(cancellationToken).ConfigureAwait(false);
                if (verifiedState is null)
                {
                    lastError = $"{method}: the simulator did not retain a stable change in the requested direction.";
                    DataSourceDebugLog.Warn(LogSource, $"Fuel write strategy not retained | method={method}");
                    if (possibleMutationObserved && lastObservedState is not null)
                    {
                        var observedDelta = lastObservedState.CurrentFuelKg - attemptBefore.CurrentFuelKg;
                        var directionMatches = Math.Abs(observedDelta) >= movementToleranceKg && Math.Sign(observedDelta) == Math.Sign(deltaKg);
                        var withinRequest = Math.Abs(observedDelta) <= Math.Abs(deltaKg) + requestToleranceKg;
                        var reconciledStatus = directionMatches && withinRequest ? AarFuelApplyStatus.Partial : AarFuelApplyStatus.Failed;
                        var error = $"{method} changed fuel, but the result did not settle to a verified value; no fallback write was attempted.";
                        DataSourceDebugLog.Warn(LogSource, $"Fuel write strategy reconciled without retry | method={method} observedKg={observedDelta:0.00} status={reconciledStatus}");
                        return new AarFuelApplyResult(deltaKg, observedDelta, reconciledStatus, error);
                    }
                    if (requestError is not null) lastError = requestError;
                    continue;
                }

                var actual = verifiedState.CurrentFuelKg - attemptBefore.CurrentFuelKg;
                lock (_gate) _preferredWriteMethod = method;
                var status = Math.Abs(actual - deltaKg) <= requestToleranceKg ? AarFuelApplyStatus.Success : AarFuelApplyStatus.Partial;
                var mutationMessage = $"Fuel mutation verified | method={method} beforeKg={attemptBefore.CurrentFuelKg:0.00} afterKg={verifiedState.CurrentFuelKg:0.00} requestedKg={deltaKg:0.00} appliedKg={actual:0.00} status={status}";
                if (status == AarFuelApplyStatus.Success) DataSourceDebugLog.Important(LogSource, mutationMessage);
                else DataSourceDebugLog.Warn(LogSource, mutationMessage);
                LogFuelState(verifiedState, logTanks: true);
                return new AarFuelApplyResult(deltaKg, actual, status,
                    status == AarFuelApplyStatus.Partial ? $"{method} applied a stable partial change." : null);
            }

            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, lastError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishFuelOperationAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FinishFuelOperationAsync(CancellationToken.None).ConfigureAwait(false);
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

    private async Task FinishFuelOperationAsync(CancellationToken cancellationToken)
    {
        try { await SendAsync("FINISH_FUEL_OPERATION", CancellationToken.None, allowFailedResponse: true).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { DataSourceDebugLog.Warn(LogSource, $"Fuel operation cleanup request failed | error={ex.Message}"); }
    }

    private async Task<AarBridgeResponse> SendAsync(string action, CancellationToken cancellationToken, double? deltaKg = null,
        bool allowFailedResponse = false, string? writeMethod = null)
    {
        if (!transport.IsConnected) throw new IOException("Microsoft Flight Simulator is not connected.");
        var request = new AarBridgeRequest(Guid.NewGuid().ToString("N"), action, SupportedProtocolVersion, ClientVersion, deltaKg, writeMethod);
        var timer = Stopwatch.StartNew();
        DataSourceDebugLog.Debug(LogSource, $"Request | action={action} requestId={request.RequestId}{(deltaKg.HasValue ? $" requestedKg={deltaKg.Value:0.00}" : string.Empty)}");
        AarBridgeResponse response;
        try { response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException ex)
        {
            var diagnostic = $"AAR Bridge did not respond to {action} within {timer.ElapsedMilliseconds} ms.";
            DataSourceDebugLog.Important(LogSource, $"Bridge request timeout | action={action} requestId={request.RequestId} timeoutMs={timer.ElapsedMilliseconds} simConnectConnected={transport.IsConnected}");
            throw new TimeoutException(diagnostic, ex);
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
        if (IsValid(state) && state.SampledAtUtc != default)
            FuelSampled?.Invoke(this, new AarFuelReading(state.CurrentFuelKg, state.CapacityKg, state.SampledAtUtc));
    }

    private void UpdateWriteMethods(AarBridgeDiagnostics? diagnostics)
    {
        if (diagnostics is null) return;
        bool canWrite;
        lock (_gate)
        {
            _writeMethods = diagnostics.WriteMethods ?? [];
            _canWrite = _writeMethods.Length > 0;
            canWrite = _canWrite;
        }
        if (RuntimeState is not (AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable)) return;
        if (canWrite && RuntimeState == AarBridgeRuntimeState.ConnectedReadOnly)
            SetRuntime(AarBridgeRuntimeState.ConnectedWritable, null);
        else if (!canWrite && RuntimeState == AarBridgeRuntimeState.ConnectedWritable)
            SetRuntime(AarBridgeRuntimeState.ConnectedReadOnly, "The current aircraft has no detected fuel write method. Dry Hookup is available.");
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
                DataSourceDebugLog.Warn(LogSource, "===== MSFS AAR runtime session ended =====");
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
