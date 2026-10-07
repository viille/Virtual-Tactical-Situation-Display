using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class SimConnectTrafficFeed : ITrafficDataFeed, IAarPoseSource, IAarFuelAdapter, IAarBridgeTransport
{
    private const string NativeSimConnectDllName = "SimConnect.dll";
    private const string LogSource = "MSFS";
    // SimConnect rejects traffic query radii above 200 km with exception 31
    // (SIMCONNECT_EXCEPTION_OUT_OF_BOUNDS).
    private const double MaxTrafficRequestRadiusMeters = 200_000.0;
    private static readonly TimeSpan TrafficStallRecoveryThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnshipFreshThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AarFuelPublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumTrafficRetention = TimeSpan.FromSeconds(2);
    private readonly TacticalDisplaySettings _settings;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(3);
    private const string AarBridgeRequestEvent = "VTSD_AAR_REQUEST";
    private const string AarBridgeResponseEvent = "VTSD_AAR_RESPONSE";
    private const uint AarBridgeResponseEventId = 1500;
    private const uint CommBusBroadcastToWasm = 1 << 1;
    private static readonly TimeSpan AarBridgeResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions AarBridgeJson = new(JsonSerializerDefaults.Web);
    private readonly object _stateLock = new();
    private readonly object _commBusLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AarBridgeResponse>> _pendingAarBridgeResponses = new(StringComparer.Ordinal);
    private readonly StringBuilder _aarBridgeResponseBuffer = new();
    private NativeSimConnectApi? _activeSimConnectApi;
    private IntPtr _activeSimConnectHandle;
    private uint _aarBridgeResponseParts;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _isRunning;
    private OwnshipState? _latestOwnship;
    private readonly Dictionary<uint, TrafficContactState> _latestTraffic = [];
    private readonly Dictionary<uint, TrafficContactState> _lastTrafficIdentitySamples = [];
    private readonly Dictionary<uint, long> _contactGenerations = [];
    private long _nextContactGeneration;
    private long _sessionGeneration;
    private readonly Dictionary<uint, int> _ghostHitCounts = [];
    private readonly HashSet<uint> _suppressedTrafficIds = [];
    private DateTimeOffset _lastOwnshipSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTrafficRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTrafficSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAarFuelPublishedAt = DateTimeOffset.MinValue;
    private AarFuelReading? _latestAarFuel;
    private bool _hasReceivedTrafficThisSession;
    private bool _aarSamplingEnabled;

    public SimConnectTrafficFeed(TacticalDisplaySettings settings)
    {
        _settings = settings;
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnshipState>? AarPoseSampled;
    public event EventHandler<AarFuelReading>? AarFuelSampled;
    event EventHandler<AarFuelReading>? IAarFuelAdapter.FuelSampled
    {
        add => AarFuelSampled += value;
        remove => AarFuelSampled -= value;
    }
    public bool AarSamplingEnabled { get => Volatile.Read(ref _aarSamplingEnabled); set => Volatile.Write(ref _aarSamplingEnabled, value); }
    public bool IsConnected { get; private set; }
    bool IAarBridgeTransport.IsConnected => IsConnected;
    public bool IsAvailable => IsConnected;
    public bool CanReadFuel => IsConnected && _latestAarFuel is not null;
    // The public MSFS SimConnect fuel SimVars are readable, but their documented Settable column is empty.
    // Fail closed until MSFS exposes a generic, acknowledged fuel write API.
    public bool CanWriteFuel => false;

    public AarFuelReading? ReadFuel()
    {
        lock (_stateLock) return _latestAarFuel;
    }

    public Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(deltaKg) || deltaKg == 0)
            return Task.FromResult(new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel delta must be finite and non-zero."));
        return Task.FromResult(new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed,
            "Generic MSFS SimConnect fuel writing is not available; the simulator was not changed."));
    }

    public async Task<AarBridgeResponse> SendAsync(AarBridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<AarBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAarBridgeResponses.TryAdd(request.RequestId, completion))
            throw new InvalidOperationException("A duplicate AAR bridge request ID is already pending.");

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, AarBridgeJson);
            var nullTerminated = new byte[payload.Length + 1];
            Buffer.BlockCopy(payload, 0, nullTerminated, 0, payload.Length);
            lock (_commBusLock)
            {
                var api = _activeSimConnectApi;
                var handle = _activeSimConnectHandle;
                if (!IsConnected || api?.CallCommBusEvent is null || handle == IntPtr.Zero)
                    throw new NotSupportedException("This SimConnect session does not expose the MSFS 2024 CommBus API required by the AAR Bridge.");
                var data = Marshal.AllocHGlobal(nullTerminated.Length);
                try
                {
                    Marshal.Copy(nullTerminated, 0, data, nullTerminated.Length);
                    var result = api.CallCommBusEvent(handle, AarBridgeRequestEvent, CommBusBroadcastToWasm, (uint)nullTerminated.Length, data);
                    if (result != 0) throw new IOException($"SimConnect_CallCommBusEvent failed with HRESULT 0x{result:X8}.");
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            return await completion.Task.WaitAsync(AarBridgeResponseTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingAarBridgeResponses.TryRemove(request.RequestId, out _);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        DataSourceDebugLog.Info(LogSource, $"Start requested | pollRateHz={_settings.PollRateHz:0.##} rangeNm={_settings.SelectedRangeNm}");
        _isRunning = true;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        DataSourceDebugLog.Info(LogSource, "Stop requested");
        _isRunning = false;
        var loopCts = _loopCts;
        var loopTask = _loopTask;
        loopCts?.Cancel();
        _loopCts = null;
        _loopTask = null;
        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        loopCts?.Dispose();
        ResetSessionTrafficState();
        SetConnected(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var dllPath = ResolveNativeDllPath();
            if (string.IsNullOrWhiteSpace(dllPath))
            {
                DataSourceDebugLog.Warn(LogSource, "No usable SimConnect DLL found");
                SetConnected(false, forceNotify: true);
                await Task.Delay(_reconnectDelay, cancellationToken);
                continue;
            }

            try
            {
                DataSourceDebugLog.Info(LogSource, $"Attempting SimConnect init using DLL '{dllPath}'");
                using var api = NativeSimConnectApi.TryCreate(dllPath);
                if (api is null)
                {
                    DataSourceDebugLog.Warn(LogSource, $"Failed to load SimConnect API from '{dllPath}'");
                    SetConnected(false, forceNotify: true);
                    await Task.Delay(_reconnectDelay, cancellationToken);
                    continue;
                }

                await PollLoopAsync(api, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DataSourceDebugLog.Info(LogSource, "Run loop canceled");
                break;
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Error(LogSource, "Unhandled exception in run loop", ex);
                SetConnected(false, forceNotify: true);
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }

    private async Task PollLoopAsync(NativeSimConnectApi api, CancellationToken cancellationToken)
    {
        var openHr = api.Open(out var simHandle, "TacticalDisplay", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (openHr != 0)
        {
            DataSourceDebugLog.Warn(LogSource, $"SimConnect open failed with HRESULT 0x{openHr:X8}");
            SetConnected(false, forceNotify: true);
            return;
        }

        try
        {
            DataSourceDebugLog.Info(LogSource, "SimConnect session opened");
            ResetSessionTrafficState();
            lock (_commBusLock)
            {
                _activeSimConnectApi = api;
                _activeSimConnectHandle = simHandle;
                _aarBridgeResponseBuffer.Clear();
                _aarBridgeResponseParts = 0;
            }
            var subscribeCommBus = api.SubscribeToCommBusEvent;
            if (subscribeCommBus is not null)
            {
                var subscribeResult = subscribeCommBus(simHandle, AarBridgeResponseEventId, AarBridgeResponseEvent);
                if (subscribeResult != 0)
                    DataSourceDebugLog.Warn(LogSource, $"AAR bridge CommBus response subscription failed | hresult=0x{subscribeResult:X8}");
            }
            api.SubscribeToSystemEvent(simHandle, (uint)SystemEventId.ObjectAdded, "ObjectAdded");
            api.SubscribeToSystemEvent(simHandle, (uint)SystemEventId.ObjectRemoved, "ObjectRemoved");
            ConfigureDataDefinitions(api, simHandle);
            SetConnected(true);

            var pollMs = (int)System.Math.Clamp(1000.0 / System.Math.Max(_settings.PollRateHz, 1), 100, 1000);
            var lastOwnshipRequestAt = DateTimeOffset.MinValue;
            var lastSnapshotAt = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                var ownshipIntervalMs = AarSamplingEnabled ? 50 : pollMs;
                if ((now - lastOwnshipRequestAt).TotalMilliseconds >= ownshipIntervalMs)
                {
                    var ownshipRequestHr = api.RequestDataOnSimObject(
                        simHandle,
                        (uint)RequestId.Ownship,
                        (uint)DefinitionId.Ownship,
                        0,
                        (uint)SimConnectPeriod.Once,
                        0,
                        0,
                        0,
                        0);
                    if (ownshipRequestHr != 0)
                    {
                        DataSourceDebugLog.Warn(
                            LogSource,
                            $"Ownship request failed; recycling SimConnect session | hresult=0x{ownshipRequestHr:X8}");
                        return;
                    }
                    lastOwnshipRequestAt = now;
                }

                if ((now - _lastTrafficRequestAt).TotalMilliseconds >= 500)
                {
                    var radiusMeters = (uint)System.Math.Clamp(
                        _settings.SelectedRangeNm * 1852.0,
                        18520.0,
                        MaxTrafficRequestRadiusMeters);
                    DataSourceDebugLog.ThrottledDebug(
                        LogSource,
                        "traffic-request",
                        TimeSpan.FromSeconds(5),
                        () => $"Requesting traffic scan | radiusMeters={radiusMeters} rangeNm={_settings.SelectedRangeNm}");
                    var trafficRequestHr = api.RequestDataOnSimObjectType(
                        simHandle,
                        (uint)RequestId.TrafficByType,
                        (uint)DefinitionId.Traffic,
                        radiusMeters,
                        (uint)SimObjectType.Aircraft);
                    if (trafficRequestHr != 0)
                    {
                        DataSourceDebugLog.Warn(
                            LogSource,
                            $"Traffic request failed; recycling SimConnect session | hresult=0x{trafficRequestHr:X8}");
                        return;
                    }

                    _lastTrafficRequestAt = now;
                }

                if (!DrainDispatch(api, simHandle))
                {
                    return;
                }

                if (ShouldRecoverFromTrafficStall(DateTimeOffset.UtcNow))
                {
                    DataSourceDebugLog.Warn(
                        LogSource,
                        $"Traffic feed stalled while ownship is fresh; recycling SimConnect session | stallSeconds={(DateTimeOffset.UtcNow - _lastTrafficSampleAt).TotalSeconds:0.0}");
                    return;
                }

                if ((now - lastSnapshotAt).TotalMilliseconds >= pollMs)
                {
                    EmitSnapshot();
                    lastSnapshotAt = now;
                }
            }
        }
        finally
        {
            DataSourceDebugLog.Info(LogSource, "Closing SimConnect session");
            lock (_commBusLock)
            {
                _activeSimConnectApi = null;
                _activeSimConnectHandle = IntPtr.Zero;
            }
            FailPendingAarBridgeRequests(new IOException("The MSFS SimConnect session closed."));
            api.Close(simHandle);
            SetConnected(false, forceNotify: true);
        }
    }

    private static void ConfigureDataDefinitions(NativeSimConnectApi api, IntPtr simHandle)
    {
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "PLANE LATITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 1);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "PLANE LONGITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 2);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "PLANE ALTITUDE", "feet", (uint)SimConnectDataType.Float64, 0, 3);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "GPS GROUND TRUE HEADING", "degrees", (uint)SimConnectDataType.Float64, 0, 4);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "GROUND VELOCITY", "knots", (uint)SimConnectDataType.Float64, 0, 5);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "MAGVAR", "degrees", (uint)SimConnectDataType.Float64, 0, 6);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "GPS GROUND TRUE TRACK", "degrees", (uint)SimConnectDataType.Float64, 0, 7);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "FUEL TOTAL QUANTITY WEIGHT", "pounds", (uint)SimConnectDataType.Float64, 0, 8);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "FUEL TOTAL CAPACITY", "gallons", (uint)SimConnectDataType.Float64, 0, 9);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Ownship, "FUEL WEIGHT PER GALLON", "pounds", (uint)SimConnectDataType.Float64, 0, 10);

        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE LATITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 11);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE LONGITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 12);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE ALTITUDE", "feet", (uint)SimConnectDataType.Float64, 0, 13);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE HEADING DEGREES TRUE", "degrees", (uint)SimConnectDataType.Float64, 0, 14);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "GROUND VELOCITY", "knots", (uint)SimConnectDataType.Float64, 0, 15);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "GPS GROUND TRUE TRACK", "degrees", (uint)SimConnectDataType.Float64, 0, 16);
    }

    private bool DrainDispatch(NativeSimConnectApi api, IntPtr simHandle)
    {
        while (api.GetNextDispatch(simHandle, out var pData, out var cbData) == 0)
        {
            if (pData == IntPtr.Zero || cbData == 0)
            {
                break;
            }

            var header = Marshal.PtrToStructure<SimConnectRecv>(pData);
            switch ((SimConnectRecvId)header.dwID)
            {
                case SimConnectRecvId.Quit:
                    DataSourceDebugLog.Warn(LogSource, "Received SimConnect quit event");
                    SetConnected(false, forceNotify: true);
                    return false;
                case SimConnectRecvId.Exception:
                    var exception = Marshal.PtrToStructure<SimConnectRecvException>(pData);
                    DataSourceDebugLog.Warn(
                        LogSource,
                        $"SimConnect exception received; recycling session | exception={exception.dwException} sendId={exception.dwSendID} index={exception.dwIndex}");
                    return false;
                case SimConnectRecvId.EventObjectAddRemove:
                    HandleObjectAddRemove(pData);
                    break;
                case SimConnectRecvId.SimobjectData:
                case SimConnectRecvId.SimobjectDataByType:
                    HandleSimobjectData(pData);
                    break;
                case SimConnectRecvId.CommBus:
                    HandleAarBridgeResponse(pData, cbData);
                    break;
            }
        }

        return true;
    }

    private void HandleAarBridgeResponse(IntPtr pData, uint byteCount)
    {
        if (byteCount <= 32) return;
        var prefix = Marshal.PtrToStructure<SimConnectRecvCommBusPrefix>(pData);
        if (prefix.EventId != AarBridgeResponseEventId || prefix.Parts == 0 || prefix.PartIndex >= prefix.Parts) return;
        var payloadLength = checked((int)byteCount - 32);
        var bytes = new byte[payloadLength];
        Marshal.Copy(IntPtr.Add(pData, 32), bytes, 0, payloadLength);
        var chunk = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        string? completeMessage = null;
        lock (_commBusLock)
        {
            if (prefix.PartIndex == 0)
            {
                _aarBridgeResponseBuffer.Clear();
                _aarBridgeResponseParts = prefix.Parts;
            }
            if (_aarBridgeResponseParts != prefix.Parts) return;
            _aarBridgeResponseBuffer.Append(chunk);
            if (prefix.PartIndex + 1 == prefix.Parts)
            {
                completeMessage = _aarBridgeResponseBuffer.ToString();
                _aarBridgeResponseBuffer.Clear();
                _aarBridgeResponseParts = 0;
            }
        }
        if (completeMessage is null) return;

        try
        {
            var response = JsonSerializer.Deserialize<AarBridgeResponse>(completeMessage, AarBridgeJson)
                ?? throw new InvalidDataException("The AAR Bridge returned an empty CommBus response.");
            if (string.IsNullOrWhiteSpace(response.RequestId) || !_pendingAarBridgeResponses.TryGetValue(response.RequestId, out var completion)) return;
            completion.TrySetResult(response);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            DataSourceDebugLog.Warn(LogSource, $"Invalid AAR bridge CommBus response: {ex.Message}");
        }
    }

    private void FailPendingAarBridgeRequests(Exception error)
    {
        foreach (var pending in _pendingAarBridgeResponses.Values) pending.TrySetException(error);
        _pendingAarBridgeResponses.Clear();
    }

    private void HandleObjectAddRemove(IntPtr pData)
    {
        var recv = Marshal.PtrToStructure<SimConnectRecvEventObjectAddRemove>(pData);
        if (recv.ObjectType != (uint)SimObjectType.Aircraft) return;
        var objectId = recv.Data;
        lock (_stateLock)
        {
            _latestTraffic.Remove(objectId);
            _contactGenerations.Remove(objectId);
            _lastTrafficIdentitySamples.Remove(objectId);
            _ghostHitCounts.Remove(objectId);
            _suppressedTrafficIds.Remove(objectId);
        }
    }

    private void HandleSimobjectData(IntPtr pData)
    {
        var recv = Marshal.PtrToStructure<SimConnectRecvSimobjectData>(pData);
        var dataOffset = Marshal.OffsetOf<SimConnectRecvSimobjectData>(nameof(SimConnectRecvSimobjectData.dwData)).ToInt32();
        var payloadPtr = IntPtr.Add(pData, dataOffset);

        if (recv.dwRequestID == (uint)RequestId.Ownship)
        {
            var ownshipRaw = Marshal.PtrToStructure<OwnshipRaw>(payloadPtr);
            var now = DateTimeOffset.UtcNow;
            OwnshipState ownship;
            lock (_stateLock)
            {
                var trueHeading = GeoMath.NormalizeDegrees(ownshipRaw.HeadingDeg);
                _lastOwnshipSampleAt = now;
                ownship = new OwnshipState(
                    "OWN",
                    ownshipRaw.Latitude,
                    ownshipRaw.Longitude,
                    ownshipRaw.AltitudeFt,
                    trueHeading,
                    ownshipRaw.SpeedKt,
                    now,
                    ownshipRaw.MagneticVariationDeg,
                    GeoMath.NormalizeDegrees(ownshipRaw.GroundTrackDeg),
                    _sessionGeneration);
                _latestOwnship = ownship;
            }
            if (AarSamplingEnabled) AarPoseSampled?.Invoke(this, ownship);
            if (now - _lastAarFuelPublishedAt >= AarFuelPublishInterval)
            {
                var currentKg = ownshipRaw.FuelWeightLbs * 0.45359237;
                var capacityKg = ownshipRaw.FuelCapacityGallons * ownshipRaw.FuelWeightPerGallonLbs * 0.45359237;
                if (double.IsFinite(currentKg) && double.IsFinite(capacityKg) && currentKg >= 0 && capacityKg > 0 && currentKg <= capacityKg + 0.1)
                {
                    var reading = new AarFuelReading(currentKg, capacityKg, now);
                    lock (_stateLock) _latestAarFuel = reading;
                    AarFuelSampled?.Invoke(this, reading);
                }
                _lastAarFuelPublishedAt = now;
            }

            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "ownship-sample",
                TimeSpan.FromSeconds(2),
                () => $"Ownship sample | lat={ownshipRaw.Latitude:F5} lon={ownshipRaw.Longitude:F5} altFt={ownshipRaw.AltitudeFt:F0} hdg={GeoMath.NormalizeDegrees(ownshipRaw.HeadingDeg):F1} spdKt={ownshipRaw.SpeedKt:F0}");
            return;
        }

        if (recv.dwRequestID == (uint)RequestId.TrafficByType)
        {
            if (recv.dwObjectID == 0)
            {
                return;
            }

            var trafficRaw = Marshal.PtrToStructure<TrafficRaw>(payloadPtr);
            var now = DateTimeOffset.UtcNow;
            if (IsLikelyOwnshipGhost(trafficRaw))
            {
                lock (_stateLock)
                {
                    _ghostHitCounts.TryGetValue(recv.dwObjectID, out var hits);
                    hits++;
                    _ghostHitCounts[recv.dwObjectID] = hits;
                    if (hits >= 2)
                    {
                        _suppressedTrafficIds.Add(recv.dwObjectID);
                        _contactGenerations.Remove(recv.dwObjectID);
                        _lastTrafficIdentitySamples.Remove(recv.dwObjectID);
                        DataSourceDebugLog.Debug(LogSource, $"Suppressing likely ownship ghost target | objectId={recv.dwObjectID}");
                    }
                    _latestTraffic.Remove(recv.dwObjectID);
                }
                return;
            }

            lock (_stateLock)
            {
                if (_suppressedTrafficIds.Remove(recv.dwObjectID))
                {
                    DataSourceDebugLog.Debug(LogSource, $"Released reused traffic object ID from ownship ghost suppression | objectId={recv.dwObjectID}");
                }

                _ghostHitCounts.Remove(recv.dwObjectID);
                _lastTrafficSampleAt = now;
                _hasReceivedTrafficThisSession = true;
                var generationChanged = !_contactGenerations.TryGetValue(recv.dwObjectID, out var generation);
                if (_lastTrafficIdentitySamples.TryGetValue(recv.dwObjectID, out var priorContact))
                {
                    var gap = now - priorContact.Timestamp;
                    var jumpNm = GeoMath.DistanceNm(priorContact.LatitudeDeg, priorContact.LongitudeDeg, trafficRaw.Latitude, trafficRaw.Longitude);
                    generationChanged = gap > TimeSpan.FromSeconds(10) ||
                        (gap > TimeSpan.Zero && jumpNm / gap.TotalHours > 2000);
                }

                if (generationChanged)
                {
                    generation = ++_nextContactGeneration;
                    _contactGenerations[recv.dwObjectID] = generation;
                }
                _latestTraffic[recv.dwObjectID] = new TrafficContactState(
                    recv.dwObjectID.ToString(),
                    null,
                    trafficRaw.Latitude,
                    trafficRaw.Longitude,
                    trafficRaw.AltitudeFt,
                    GeoMath.NormalizeDegrees(trafficRaw.HeadingDeg),
                    trafficRaw.SpeedKt,
                    now,
                    false,
                    generation,
                    GeoMath.NormalizeDegrees(trafficRaw.GroundTrackDeg));
                _lastTrafficIdentitySamples[recv.dwObjectID] = _latestTraffic[recv.dwObjectID];
            }

            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "traffic-sample",
                TimeSpan.FromSeconds(3),
                () => $"Traffic sample | objectId={recv.dwObjectID} lat={trafficRaw.Latitude:F5} lon={trafficRaw.Longitude:F5} altFt={trafficRaw.AltitudeFt:F0} hdg={GeoMath.NormalizeDegrees(trafficRaw.HeadingDeg):F1} spdKt={trafficRaw.SpeedKt:F0}");
        }
    }

    private void ResetSessionTrafficState()
    {
        lock (_stateLock)
        {
            _latestOwnship = null;
            _latestAarFuel = null;
            _latestTraffic.Clear();
            _lastTrafficIdentitySamples.Clear();
            _contactGenerations.Clear();
            _nextContactGeneration++;
            _sessionGeneration++;
            _ghostHitCounts.Clear();
            _suppressedTrafficIds.Clear();
            _lastOwnshipSampleAt = DateTimeOffset.MinValue;
            _lastTrafficRequestAt = DateTimeOffset.MinValue;
            _lastTrafficSampleAt = DateTimeOffset.MinValue;
            _hasReceivedTrafficThisSession = false;
            _lastAarFuelPublishedAt = DateTimeOffset.MinValue;
        }

        DataSourceDebugLog.Info(LogSource, "Reset SimConnect session traffic state");
    }

    private bool ShouldRecoverFromTrafficStall(DateTimeOffset now)
    {
        DateTimeOffset lastOwnshipSampleAt;
        DateTimeOffset lastTrafficRequestAt;
        DateTimeOffset lastTrafficSampleAt;
        bool hasReceivedTrafficThisSession;

        lock (_stateLock)
        {
            lastOwnshipSampleAt = _lastOwnshipSampleAt;
            lastTrafficRequestAt = _lastTrafficRequestAt;
            lastTrafficSampleAt = _lastTrafficSampleAt;
            hasReceivedTrafficThisSession = _hasReceivedTrafficThisSession;
        }

        if (!hasReceivedTrafficThisSession)
        {
            return false;
        }

        if (lastOwnshipSampleAt == DateTimeOffset.MinValue ||
            now - lastOwnshipSampleAt > OwnshipFreshThreshold)
        {
            return false;
        }

        if (lastTrafficRequestAt == DateTimeOffset.MinValue ||
            now - lastTrafficRequestAt > OwnshipFreshThreshold)
        {
            return false;
        }

        return lastTrafficSampleAt != DateTimeOffset.MinValue &&
            now - lastTrafficSampleAt > TrafficStallRecoveryThreshold;
    }

    private bool IsLikelyOwnshipGhost(TrafficRaw trafficRaw)
    {
        OwnshipState? ownship;
        lock (_stateLock)
        {
            ownship = _latestOwnship;
        }

        if (ownship is null)
        {
            return false;
        }

        var dLat = System.Math.Abs(ownship.LatitudeDeg - trafficRaw.Latitude);
        var dLon = System.Math.Abs(ownship.LongitudeDeg - trafficRaw.Longitude);
        var dAlt = System.Math.Abs(ownship.AltitudeFt - trafficRaw.AltitudeFt);
        var dSpd = System.Math.Abs((ownship.SpeedKt ?? 0) - trafficRaw.SpeedKt);

        // Heuristic ownship ghost filter:
        // same coordinates (very close) and similar altitude/speed.
        return dLat < 0.0001 && dLon < 0.0001 && dAlt < 200 && dSpd < 25;
    }

    private void EmitSnapshot()
    {
        OwnshipState? ownship;
        IReadOnlyList<TrafficContactState> traffic;
        var now = DateTimeOffset.UtcNow;
        lock (_stateLock)
        {
            PruneExpiredTraffic(now);
            ownship = _latestOwnship;
            traffic = _latestTraffic.Values.ToList();
        }

        if (ownship is null)
        {
            return;
        }

        var filteredTraffic = traffic
            .Where(t => !IsLikelyOwnshipMirror(ownship, t))
            .ToList();

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "snapshot-summary",
            TimeSpan.FromSeconds(2),
            () => $"Snapshot emitted | trafficCount={filteredTraffic.Count} rawTrafficCount={traffic.Count}");

        SnapshotReceived?.Invoke(this, new TrafficSnapshot(ownship, filteredTraffic, now));
    }

    private void PruneExpiredTraffic(DateTimeOffset now)
    {
        var retention = TimeSpan.FromSeconds(Math.Max(_settings.RemoveAfterSeconds, MinimumTrafficRetention.TotalSeconds));
        var cutoff = now - retention;
        foreach (var objectId in _latestTraffic
                     .Where(pair => pair.Value.Timestamp < cutoff)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _latestTraffic.Remove(objectId);
            _ghostHitCounts.Remove(objectId);
            _suppressedTrafficIds.Remove(objectId);
        }
    }

    private static bool IsLikelyOwnshipMirror(OwnshipState ownship, TrafficContactState target)
    {
        var dLat = System.Math.Abs(ownship.LatitudeDeg - target.LatitudeDeg);
        var dLon = System.Math.Abs(ownship.LongitudeDeg - target.LongitudeDeg);
        var dAlt = System.Math.Abs(ownship.AltitudeFt - target.AltitudeFt);
        var dSpd = System.Math.Abs((ownship.SpeedKt ?? 0) - (target.SpeedKt ?? 0));
        var dHdg = target.HeadingDeg.HasValue
            ? System.Math.Abs(NormalizeHeadingDelta(ownship.HeadingDeg, target.HeadingDeg.Value))
            : 180.0;

        return dLat < 0.0002 && dLon < 0.0002 && dAlt < 300 && dSpd < 40 && dHdg < 12;
    }

    private static double NormalizeHeadingDelta(double a, double b)
    {
        var d = (a - b) % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    private string? ResolveNativeDllPath()
    {
        if (CanUseDll(NativeSimConnectDllName))
        {
            return NativeSimConnectDllName;
        }

        if (CanUseDll(_settings.PreferredSimConnectDllPath))
        {
            return _settings.PreferredSimConnectDllPath;
        }

        var envPath = Environment.GetEnvironmentVariable("MSFS_SIMCONNECT_DLL");
        if (CanUseDll(envPath))
        {
            return envPath;
        }

        var autoPath = FindNativeSimConnectDllPath(_settings.MsfsExePath);
        if (!string.IsNullOrWhiteSpace(autoPath))
        {
            _settings.PreferredSimConnectDllPath = autoPath;
            return autoPath;
        }

        return null;
    }

    private void SetConnected(bool value, bool forceNotify = false)
    {
        if (IsConnected == value && !forceNotify)
        {
            return;
        }

        IsConnected = value;
        DataSourceDebugLog.Info(LogSource, $"Connection state changed | connected={value}");
        ConnectionChanged?.Invoke(this, value);
    }

    public static bool CanUseDll(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return false;
        }

        try
        {
            using var api = NativeSimConnectApi.TryCreate(dllPath);
            return api is not null;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Error(LogSource, $"Failed to probe DLL '{dllPath}'", ex);
            return false;
        }
    }

    public static string BuildDiagnosticReport(TacticalDisplaySettings? settings = null)
    {
        var configured = settings?.PreferredSimConnectDllPath;
        var autoPath = FindNativeSimConnectDllPath(settings?.MsfsExePath);
        var probe = CanUseDll(NativeSimConnectDllName)
            ? NativeSimConnectDllName
            : !string.IsNullOrWhiteSpace(configured) ? configured : autoPath;

        var lines = new List<string>
        {
            "Mode: SimConnect Native API",
            $"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}",
            $"OS: {Environment.OSVersion}",
            $"Debug log: {DataSourceDebugLog.CurrentLogFilePath}",
            $"Bundled SimConnect.dll: {CanUseDll(NativeSimConnectDllName)}",
            $"Configured MSFS.exe: {settings?.MsfsExePath ?? "<not set>"}",
            $"Configured SimConnect.dll: {configured ?? "<not set>"}",
            $"MSFS_SIMCONNECT_DLL: {Environment.GetEnvironmentVariable("MSFS_SIMCONNECT_DLL") ?? "<not set>"}",
            $"Auto-scan path: {autoPath ?? "<not found>"}",
            $"Probe DLL path: {probe ?? "<none>"}",
            $"Probe DLL can load API: {CanUseDll(probe)}",
            $"Probe open session: {ProbeOpenSession(probe)}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    public static string? TryResolveDllFromMsfsExe(string? msfsExePath)
    {
        if (string.IsNullOrWhiteSpace(msfsExePath) || !File.Exists(msfsExePath))
        {
            return null;
        }

        return FindNativeSimConnectDllPath(msfsExePath);
    }

    private static string? FindNativeSimConnectDllPath(string? msfsExePath)
    {
        foreach (var basePath in BuildSearchPaths(msfsExePath))
        {
            try
            {
                var candidate = Path.Combine(basePath, NativeSimConnectDllName);
                if (CanUseDll(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildSearchPaths(string? msfsExePath)
    {
        var paths = new List<string>();

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            paths.Add(AppContext.BaseDirectory);
        }

        paths.Add(Path.Combine(AppContext.BaseDirectory, "simconnect"));

        if (!string.IsNullOrWhiteSpace(msfsExePath))
        {
            var exeDir = Path.GetDirectoryName(msfsExePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                paths.Add(exeDir);
                paths.Add(Path.Combine(exeDir, "Package"));
                var parent = Directory.GetParent(exeDir)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    paths.Add(parent);
                    paths.Add(Path.Combine(parent, "MSFS SDK", "SimConnect SDK", "lib"));
                    paths.Add(Path.Combine(parent, "MSFS SDK", "SimConnect SDK", "lib", "x64"));
                    paths.Add(Path.Combine(parent, "SDK", "SimConnect SDK", "lib"));
                    paths.Add(Path.Combine(parent, "SDK", "SimConnect SDK", "lib", "x64"));
                }
            }
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf))
        {
            paths.Add(Path.Combine(pf, "MSFS SDK", "SimConnect SDK", "lib"));
            paths.Add(Path.Combine(pf, "MSFS SDK", "SimConnect SDK", "lib", "x64"));
            paths.Add(Path.Combine(pf, "Microsoft Games", "Microsoft Flight Simulator SDK", "SimConnect SDK", "lib"));
            paths.Add(Path.Combine(pf, "Microsoft Games", "Microsoft Flight Simulator SDK", "SimConnect SDK", "lib", "x64"));
        }

        if (!string.IsNullOrWhiteSpace(pfx))
        {
            paths.Add(Path.Combine(pfx, "MSFS SDK", "SimConnect SDK", "lib"));
            paths.Add(Path.Combine(pfx, "MSFS SDK", "SimConnect SDK", "lib", "x64"));
            paths.Add(Path.Combine(pfx, "Microsoft Games", "Microsoft Flight Simulator SDK", "SimConnect SDK", "lib"));
            paths.Add(Path.Combine(pfx, "Microsoft Games", "Microsoft Flight Simulator SDK", "SimConnect SDK", "lib", "x64"));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ProbeOpenSession(string? dllPath)
    {
        if (!CanUseDll(dllPath))
        {
            return "FAIL - DLL not usable";
        }

        try
        {
            using var api = NativeSimConnectApi.TryCreate(dllPath!);
            if (api is null)
            {
                return "FAIL - API load";
            }

            var hr = api.Open(out var simHandle, "Probe", IntPtr.Zero, 0, IntPtr.Zero, 0);
            if (hr == 0 && simHandle != IntPtr.Zero)
            {
                api.Close(simHandle);
                return "OK";
            }

            return $"FAIL - HRESULT 0x{hr:X8}";
        }
        catch (Exception ex)
        {
            return $"FAIL - {ex.GetType().Name}: {ex.Message}";
        }
    }

    private sealed class NativeSimConnectApi : IDisposable
    {
        private readonly IntPtr _libHandle;
        public SimConnectOpenDelegate Open { get; }
        public SimConnectCloseDelegate Close { get; }
        public SimConnectAddToDataDefinitionDelegate AddToDataDefinition { get; }
        public SimConnectRequestDataOnSimObjectDelegate RequestDataOnSimObject { get; }
        public SimConnectRequestDataOnSimObjectTypeDelegate RequestDataOnSimObjectType { get; }
        public SimConnectGetNextDispatchDelegate GetNextDispatch { get; }
        public SimConnectSubscribeToSystemEventDelegate SubscribeToSystemEvent { get; }
        public SimConnectCallCommBusEventDelegate? CallCommBusEvent { get; }
        public SimConnectSubscribeToCommBusEventDelegate? SubscribeToCommBusEvent { get; }

        private NativeSimConnectApi(
            IntPtr libHandle,
            SimConnectOpenDelegate open,
            SimConnectCloseDelegate close,
            SimConnectAddToDataDefinitionDelegate addToDef,
            SimConnectRequestDataOnSimObjectDelegate requestOnObject,
            SimConnectRequestDataOnSimObjectTypeDelegate requestByType,
            SimConnectGetNextDispatchDelegate getNextDispatch,
            SimConnectSubscribeToSystemEventDelegate subscribeToSystemEvent,
            SimConnectCallCommBusEventDelegate? callCommBusEvent,
            SimConnectSubscribeToCommBusEventDelegate? subscribeToCommBusEvent)
        {
            _libHandle = libHandle;
            Open = open;
            Close = close;
            AddToDataDefinition = addToDef;
            RequestDataOnSimObject = requestOnObject;
            RequestDataOnSimObjectType = requestByType;
            GetNextDispatch = getNextDispatch;
            SubscribeToSystemEvent = subscribeToSystemEvent;
            CallCommBusEvent = callCommBusEvent;
            SubscribeToCommBusEvent = subscribeToCommBusEvent;
        }

        public static NativeSimConnectApi? TryCreate(string dllPath)
        {
            try
            {
                var handle = NativeLibrary.Load(dllPath);
                var open = GetDelegate<SimConnectOpenDelegate>(handle, "SimConnect_Open");
                var close = GetDelegate<SimConnectCloseDelegate>(handle, "SimConnect_Close");
                var addToDef = GetDelegate<SimConnectAddToDataDefinitionDelegate>(handle, "SimConnect_AddToDataDefinition");
                var requestOnObject = GetDelegate<SimConnectRequestDataOnSimObjectDelegate>(handle, "SimConnect_RequestDataOnSimObject");
                var requestByType = GetDelegate<SimConnectRequestDataOnSimObjectTypeDelegate>(handle, "SimConnect_RequestDataOnSimObjectType");
                var getNextDispatch = GetDelegate<SimConnectGetNextDispatchDelegate>(handle, "SimConnect_GetNextDispatch");
                var subscribeToSystemEvent = GetDelegate<SimConnectSubscribeToSystemEventDelegate>(handle, "SimConnect_SubscribeToSystemEvent");
                var callCommBusEvent = TryGetDelegate<SimConnectCallCommBusEventDelegate>(handle, "SimConnect_CallCommBusEvent");
                var subscribeToCommBusEvent = TryGetDelegate<SimConnectSubscribeToCommBusEventDelegate>(handle, "SimConnect_SubscribeToCommBusEvent");
                return new NativeSimConnectApi(handle, open, close, addToDef, requestOnObject, requestByType, getNextDispatch, subscribeToSystemEvent,
                    callCommBusEvent, subscribeToCommBusEvent);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Error(LogSource, $"Native API load failed for '{dllPath}'", ex);
                return null;
            }
        }

        private static T GetDelegate<T>(IntPtr libHandle, string exportName) where T : Delegate
        {
            var fnPtr = NativeLibrary.GetExport(libHandle, exportName);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        private static T? TryGetDelegate<T>(IntPtr libHandle, string exportName) where T : Delegate =>
            NativeLibrary.TryGetExport(libHandle, exportName, out var function)
                ? Marshal.GetDelegateForFunctionPointer<T>(function)
                : null;

        public void Dispose()
        {
            if (_libHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libHandle);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectOpenDelegate(
        out IntPtr phSimConnect,
        string szName,
        IntPtr hWnd,
        uint UserEventWin32,
        IntPtr hEventHandle,
        uint ConfigIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectCloseDelegate(IntPtr hSimConnect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectAddToDataDefinitionDelegate(
        IntPtr hSimConnect,
        uint DefineID,
        string DatumName,
        string UnitsName,
        uint DatumType,
        float fEpsilon,
        uint DatumID);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectRequestDataOnSimObjectDelegate(
        IntPtr hSimConnect,
        uint RequestID,
        uint DefineID,
        uint ObjectID,
        uint Period,
        uint Flags,
        uint origin,
        uint interval,
        uint limit);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectRequestDataOnSimObjectTypeDelegate(
        IntPtr hSimConnect,
        uint RequestID,
        uint DefineID,
        uint dwRadiusMeters,
        uint type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectGetNextDispatchDelegate(
        IntPtr hSimConnect,
        out IntPtr ppData,
        out uint pcbData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectSubscribeToSystemEventDelegate(IntPtr hSimConnect, uint EventID, string SystemEventName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectCallCommBusEventDelegate(IntPtr hSimConnect, string EventName, uint BroadcastTo, uint BufferSize, IntPtr Data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectSubscribeToCommBusEventDelegate(IntPtr hSimConnect, uint EventID, string EventName);

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecv
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecvCommBusPrefix
    {
        public uint Size;
        public uint Version;
        public uint Id;
        public uint RequestId;
        public uint ArraySize;
        public uint PartIndex;
        public uint Parts;
        public uint EventId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecvSimobjectData
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
        public uint dwRequestID;
        public uint dwObjectID;
        public uint dwDefineID;
        public uint dwFlags;
        public uint dwentrynumber;
        public uint dwoutof;
        public uint dwDefineCount;
        public uint dwData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecvException
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
        public uint dwException;
        public uint dwSendID;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecvEventObjectAddRemove
    {
        public uint Size;
        public uint Version;
        public uint Id;
        public uint GroupId;
        public uint EventId;
        public uint Data;
        public uint ObjectType;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OwnshipRaw
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFt;
        public double HeadingDeg;
        public double SpeedKt;
        public double MagneticVariationDeg;
        public double GroundTrackDeg;
        public double FuelWeightLbs;
        public double FuelCapacityGallons;
        public double FuelWeightPerGallonLbs;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TrafficRaw
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFt;
        public double HeadingDeg;
        public double SpeedKt;
        public double GroundTrackDeg;
    }

    private enum SimConnectRecvId : uint
    {
        Null = 0,
        Exception = 1,
        Open = 2,
        Quit = 3,
        Event = 4,
        EventObjectAddRemove = 5,
        EventFilename = 6,
        EventFrame = 7,
        SimobjectData = 8,
        SimobjectDataByType = 9,
        CommBus = 44
    }

    private enum SystemEventId : uint
    {
        ObjectAdded = 500,
        ObjectRemoved = 501
    }

    private enum SimConnectPeriod : uint
    {
        Never = 0,
        Once = 1
    }

    private enum SimObjectType : uint
    {
        User = 0,
        All = 1,
        Aircraft = 2
    }

    private enum SimConnectDataType : uint
    {
        Float64 = 4,
        String32 = 6
    }

    private enum DefinitionId : uint
    {
        Ownship = 1,
        Traffic = 2
    }

    private enum RequestId : uint
    {
        Ownship = 100,
        TrafficByType = 200
    }
}
