using System.IO;
using System.Runtime.InteropServices;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class SimConnectTrafficFeed : ITrafficDataFeed
{
    private const string NativeSimConnectDllName = "SimConnect.dll";
    private const string LogSource = "MSFS";
    private readonly TacticalDisplaySettings _settings;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(3);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _loopCts;
    private bool _isRunning;
    private OwnshipState? _latestOwnship;
    private readonly Dictionary<uint, TrafficContactState> _latestTraffic = [];
    private readonly Dictionary<uint, int> _ghostHitCounts = [];
    private readonly HashSet<uint> _suppressedTrafficIds = [];

    public SimConnectTrafficFeed(TacticalDisplaySettings settings)
    {
        _settings = settings;
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        DataSourceDebugLog.Info(LogSource, $"Start requested | pollRateHz={_settings.PollRateHz:0.##} rangeNm={_settings.SelectedRangeNm}");
        _isRunning = true;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        DataSourceDebugLog.Info(LogSource, "Stop requested");
        _isRunning = false;
        _loopCts?.Cancel();
        SetConnected(false);
        return Task.CompletedTask;
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
            ConfigureDataDefinitions(api, simHandle);
            SetConnected(true);

            var pollMs = (int)System.Math.Clamp(1000.0 / System.Math.Max(_settings.PollRateHz, 1), 100, 1000);
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));
            var lastTrafficRequestAt = DateTimeOffset.MinValue;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                api.RequestDataOnSimObject(
                    simHandle,
                    (uint)RequestId.Ownship,
                    (uint)DefinitionId.Ownship,
                    0,
                    (uint)SimConnectPeriod.Once,
                    0,
                    0,
                    0,
                    0);

                var now = DateTimeOffset.UtcNow;
                if ((now - lastTrafficRequestAt).TotalMilliseconds >= 500)
                {
                    var radiusMeters = (uint)System.Math.Clamp(_settings.SelectedRangeNm * 1852.0, 18520.0, 222240.0);
                    DataSourceDebugLog.ThrottledDebug(
                        LogSource,
                        "traffic-request",
                        TimeSpan.FromSeconds(5),
                        () => $"Requesting traffic scan | radiusMeters={radiusMeters} rangeNm={_settings.SelectedRangeNm}");
                    api.RequestDataOnSimObjectType(
                        simHandle,
                        (uint)RequestId.TrafficByType,
                        (uint)DefinitionId.Traffic,
                        radiusMeters,
                        (uint)SimObjectType.Aircraft);
                    lastTrafficRequestAt = now;
                }

                DrainDispatch(api, simHandle);
                EmitSnapshot();
            }
        }
        finally
        {
            DataSourceDebugLog.Info(LogSource, "Closing SimConnect session");
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

        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE LATITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 11);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE LONGITUDE", "degrees", (uint)SimConnectDataType.Float64, 0, 12);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE ALTITUDE", "feet", (uint)SimConnectDataType.Float64, 0, 13);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "PLANE HEADING DEGREES TRUE", "degrees", (uint)SimConnectDataType.Float64, 0, 14);
        api.AddToDataDefinition(simHandle, (uint)DefinitionId.Traffic, "GROUND VELOCITY", "knots", (uint)SimConnectDataType.Float64, 0, 15);
    }

    private void DrainDispatch(NativeSimConnectApi api, IntPtr simHandle)
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
                    return;
                case SimConnectRecvId.SimobjectData:
                case SimConnectRecvId.SimobjectDataByType:
                    HandleSimobjectData(pData);
                    break;
            }
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
            lock (_stateLock)
            {
                _latestOwnship = new OwnshipState(
                    "OWN",
                    ownshipRaw.Latitude,
                    ownshipRaw.Longitude,
                    ownshipRaw.AltitudeFt,
                    GeoMath.NormalizeDegrees(ownshipRaw.HeadingDeg),
                    ownshipRaw.SpeedKt,
                    now);
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

            lock (_stateLock)
            {
                if (_suppressedTrafficIds.Contains(recv.dwObjectID))
                {
                    _latestTraffic.Remove(recv.dwObjectID);
                    return;
                }
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
                        DataSourceDebugLog.Debug(LogSource, $"Suppressing likely ownship ghost target | objectId={recv.dwObjectID}");
                    }
                    _latestTraffic.Remove(recv.dwObjectID);
                }
                return;
            }

            lock (_stateLock)
            {
                _ghostHitCounts.Remove(recv.dwObjectID);
                _latestTraffic[recv.dwObjectID] = new TrafficContactState(
                    recv.dwObjectID.ToString(),
                    null,
                    trafficRaw.Latitude,
                    trafficRaw.Longitude,
                    trafficRaw.AltitudeFt,
                    GeoMath.NormalizeDegrees(trafficRaw.HeadingDeg),
                    trafficRaw.SpeedKt,
                    now);
            }

            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "traffic-sample",
                TimeSpan.FromSeconds(3),
                () => $"Traffic sample | objectId={recv.dwObjectID} lat={trafficRaw.Latitude:F5} lon={trafficRaw.Longitude:F5} altFt={trafficRaw.AltitudeFt:F0} hdg={GeoMath.NormalizeDegrees(trafficRaw.HeadingDeg):F1} spdKt={trafficRaw.SpeedKt:F0}");
        }
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
        lock (_stateLock)
        {
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

        SnapshotReceived?.Invoke(this, new TrafficSnapshot(ownship, filteredTraffic, DateTimeOffset.UtcNow));
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

        private NativeSimConnectApi(
            IntPtr libHandle,
            SimConnectOpenDelegate open,
            SimConnectCloseDelegate close,
            SimConnectAddToDataDefinitionDelegate addToDef,
            SimConnectRequestDataOnSimObjectDelegate requestOnObject,
            SimConnectRequestDataOnSimObjectTypeDelegate requestByType,
            SimConnectGetNextDispatchDelegate getNextDispatch)
        {
            _libHandle = libHandle;
            Open = open;
            Close = close;
            AddToDataDefinition = addToDef;
            RequestDataOnSimObject = requestOnObject;
            RequestDataOnSimObjectType = requestByType;
            GetNextDispatch = getNextDispatch;
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
                return new NativeSimConnectApi(handle, open, close, addToDef, requestOnObject, requestByType, getNextDispatch);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectRecv
    {
        public uint dwSize;
        public uint dwVersion;
        public uint dwID;
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OwnshipRaw
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFt;
        public double HeadingDeg;
        public double SpeedKt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TrafficRaw
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFt;
        public double HeadingDeg;
        public double SpeedKt;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Callsign;
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
        SimobjectDataByType = 9
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
