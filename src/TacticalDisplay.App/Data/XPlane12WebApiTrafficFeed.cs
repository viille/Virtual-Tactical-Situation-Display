using System.Net.Http;
using System.Text.Json;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class XPlane12WebApiTrafficFeed : ITrafficDataFeed, IAarPoseSource
{
    private const string LogSource = "XPlane12";
    private const int MaxTcasTargets = 63;
    private const int MaxMultiplayerTargets = 19;
    private const double MetersPerNauticalMile = 1852.0;
    private const double FeetPerMeter = 3.280839895;
    private const double KnotsPerMeterPerSecond = 1.9438444924406;

    private static readonly string[] RequiredOwnshipDataRefs =
    [
        "sim/flightmodel/position/latitude",
        "sim/flightmodel/position/longitude",
        "sim/flightmodel/position/elevation",
        "sim/flightmodel/position/true_psi",
        "sim/flightmodel/position/hpath",
        "sim/flightmodel/position/groundspeed"
    ];

    private static readonly string[] OptionalTrafficDataRefs =
    [
        "sim/cockpit2/tcas/targets/modeS_id",
        "sim/cockpit2/tcas/targets/position/lat",
        "sim/cockpit2/tcas/targets/position/lon",
        "sim/cockpit2/tcas/targets/position/ele",
        "sim/cockpit2/tcas/indicators/relative_bearing_degs",
        "sim/cockpit2/tcas/indicators/relative_distance_mtrs",
        "sim/cockpit2/tcas/indicators/relative_altitude_mtrs",
        "sim/cockpit2/tcas/targets/position/hpath",
        "sim/cockpit2/tcas/targets/position/psi",
        "sim/cockpit2/tcas/targets/position/V_msc"
    ];

    private readonly TacticalDisplaySettings _settings;
    private readonly XPlane12WebApiClient _webApi;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _isRunning;
    private bool _isConnected;
    private XPlane12AarFuelAdapter? _aarFuelAdapter;
    private bool _aarSamplingEnabled;
    private DateTimeOffset _lastFuelRefreshAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, long> _dataRefIds = new(StringComparer.Ordinal);
    private DateTimeOffset _lastMultiplayerFallbackReadAt = DateTimeOffset.MinValue;
    private IReadOnlyList<TrafficContactState> _latestMultiplayerFallbackTraffic = [];

    public XPlane12WebApiTrafficFeed(TacticalDisplaySettings settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _webApi = new XPlane12WebApiClient(settings.XPlane12ApiBaseUrl, httpClient);
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnshipState>? AarPoseSampled;
    public bool IsConnected => _isConnected;
    public bool AarSamplingEnabled { get => _aarSamplingEnabled; set => _aarSamplingEnabled = value; }

    internal XPlane12WebApiClient WebApi => _webApi;
    internal void AttachAarFuelAdapter(XPlane12AarFuelAdapter adapter) => _aarFuelAdapter = adapter;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        DataSourceDebugLog.Info(LogSource, $"Start requested | apiBaseUrl={GetBaseUri()} pollRateHz={_settings.PollRateHz:0.##}");
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
        _aarFuelAdapter?.MarkUnavailable();
        SetConnected(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _webApi.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await InitializeAsync(cancellationToken);
                SetConnected(true);
                await PollLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DataSourceDebugLog.Info(LogSource, "Run loop canceled");
                break;
            }
            catch (OperationCanceledException ex)
            {
                DataSourceDebugLog.Info(LogSource, $"X-Plane Web API request timed out; reconnecting | error={ex.Message}");
                SetConnected(false, forceNotify: true);
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Error(LogSource, "Unhandled exception in XP12 feed", ex);
                SetConnected(false, forceNotify: true);
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await DiscoverApiVersionAsync(cancellationToken);
        _dataRefIds.Clear();

        foreach (var dataRefName in RequiredOwnshipDataRefs)
        {
            _dataRefIds[dataRefName] = await ResolveDataRefIdAsync(dataRefName, cancellationToken);
        }

        await TryResolveOptionalDataRefAsync("sim/flightmodel/position/mag_psi", cancellationToken);
        if (_aarFuelAdapter is not null)
        {
            try { await _aarFuelAdapter.InitializeAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _aarFuelAdapter.MarkUnavailable();
                DataSourceDebugLog.Info(LogSource, $"XP12 AAR fuel adapter is unavailable; traffic continues | error={ex.Message}");
            }
        }
        foreach (var dataRefName in OptionalTrafficDataRefs)
        {
            try
            {
                _dataRefIds[dataRefName] = await ResolveDataRefIdAsync(dataRefName, cancellationToken);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Info(LogSource, $"Optional traffic dataref unavailable | name={dataRefName} error={ex.Message}");
            }
        }

        for (var i = 1; i <= MaxMultiplayerTargets; i++)
        {
            await TryResolveOptionalDataRefAsync($"sim/multiplayer/position/plane{i}_lat", cancellationToken);
            await TryResolveOptionalDataRefAsync($"sim/multiplayer/position/plane{i}_lon", cancellationToken);
            await TryResolveOptionalDataRefAsync($"sim/multiplayer/position/plane{i}_el", cancellationToken);
            await TryResolveOptionalDataRefAsync($"sim/multiplayer/position/plane{i}_psi", cancellationToken);
        }

        var tcasRefs = OptionalTrafficDataRefs.Count(_dataRefIds.ContainsKey);
        var multiplayerRefs = Enumerable.Range(1, MaxMultiplayerTargets)
            .SelectMany(i => new[]
            {
                $"sim/multiplayer/position/plane{i}_lat",
                $"sim/multiplayer/position/plane{i}_lon",
                $"sim/multiplayer/position/plane{i}_el",
                $"sim/multiplayer/position/plane{i}_psi"
            })
            .Count(_dataRefIds.ContainsKey);

        DataSourceDebugLog.Info(LogSource, $"Resolved XP12 datarefs via {_webApi.ApiVersion} API | trafficRefs={tcasRefs}/{OptionalTrafficDataRefs.Length} multiplayerRefs={multiplayerRefs}/{MaxMultiplayerTargets * 4}");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var normalPollMs = (int)Math.Clamp(1000.0 / Math.Max(_settings.PollRateHz, 1), 100, 1000);
        var nextSampleAt = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            var targetInterval = TimeSpan.FromMilliseconds(AarSamplingEnabled ? 100 : normalPollMs);
            if (now < nextSampleAt) continue;
            nextSampleAt = now + targetInterval;
            var snapshot = await ReadSnapshotAsync(cancellationToken);
            if (AarSamplingEnabled) AarPoseSampled?.Invoke(this, snapshot.Ownship);
            if (_aarFuelAdapter is not null && now - _lastFuelRefreshAt >= TimeSpan.FromSeconds(1))
            {
                _lastFuelRefreshAt = now;
                try { await _aarFuelAdapter.RefreshFuelAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { DataSourceDebugLog.ThrottledDebug(LogSource, "aar-fuel-refresh", TimeSpan.FromSeconds(10), () => ex.Message); }
            }
            SnapshotReceived?.Invoke(this, snapshot);
        }
    }

    private async Task<TrafficSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ownshipTask = ReadOwnshipAsync(now, cancellationToken);
        var modeSTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/modeS_id", cancellationToken);
        var latitudesTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/lat", cancellationToken);
        var longitudesTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/lon", cancellationToken);
        var elevationsTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/ele", cancellationToken);
        var bearingTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_bearing_degs", cancellationToken);
        var distanceTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_distance_mtrs", cancellationToken);
        var altitudeTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_altitude_mtrs", cancellationToken);
        var trackTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/hpath", cancellationToken);
        var headingTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/psi", cancellationToken);
        var speedTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/V_msc", cancellationToken);

        await Task.WhenAll(
            ownshipTask,
            modeSTask,
            latitudesTask,
            longitudesTask,
            elevationsTask,
            bearingTask,
            distanceTask,
            altitudeTask,
            trackTask,
            headingTask,
            speedTask);

        var ownship = ownshipTask.Result;
        var contacts = BuildDirectTcasTrafficContacts(
            ownship,
            now,
            modeSTask.Result,
            latitudesTask.Result,
            longitudesTask.Result,
            elevationsTask.Result,
            trackTask.Result,
            headingTask.Result,
            speedTask.Result);

        var source = "tcas-position";
        if (contacts.Count == 0)
        {
            contacts = BuildRelativeTcasTrafficContacts(
                ownship,
                now,
                modeSTask.Result,
                bearingTask.Result,
                distanceTask.Result,
                altitudeTask.Result,
                trackTask.Result,
                headingTask.Result,
                speedTask.Result);
            source = "tcas-relative";
        }

        if (contacts.Count == 0)
        {
            contacts = await ReadMultiplayerFallbackTrafficAsync(ownship, now, cancellationToken);
            source = "multiplayer";
        }

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "snapshot-summary",
            TimeSpan.FromSeconds(2),
            () => $"Snapshot emitted | trafficCount={contacts.Count} source={source} apiVersion={_webApi.ApiVersion}");

        return new TrafficSnapshot(ownship, contacts, now);
    }

    private async Task<IReadOnlyList<TrafficContactState>> ReadMultiplayerFallbackTrafficAsync(
        OwnshipState ownship,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if ((timestamp - _lastMultiplayerFallbackReadAt).TotalMilliseconds < 1000)
        {
            return _latestMultiplayerFallbackTraffic;
        }

        var tasks = Enumerable.Range(1, MaxMultiplayerTargets)
            .Select(i => ReadMultiplayerTargetAsync(i, ownship, timestamp, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);

        _latestMultiplayerFallbackTraffic = tasks
            .Select(task => task.Result)
            .Where(contact => contact is not null)
            .Cast<TrafficContactState>()
            .ToList();
        _lastMultiplayerFallbackReadAt = timestamp;

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "multiplayer-fallback-summary",
            TimeSpan.FromSeconds(5),
            () => $"Multiplayer fallback read complete | trafficCount={_latestMultiplayerFallbackTraffic.Count}");

        return _latestMultiplayerFallbackTraffic;
    }

    private async Task<TrafficContactState?> ReadMultiplayerTargetAsync(
        int index,
        OwnshipState ownship,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var latitudeTask = GetOptionalDoubleAsync($"sim/multiplayer/position/plane{index}_lat", cancellationToken);
        var longitudeTask = GetOptionalDoubleAsync($"sim/multiplayer/position/plane{index}_lon", cancellationToken);
        var elevationTask = GetOptionalDoubleAsync($"sim/multiplayer/position/plane{index}_el", cancellationToken);
        var headingTask = GetOptionalDoubleAsync($"sim/multiplayer/position/plane{index}_psi", cancellationToken);

        await Task.WhenAll(latitudeTask, longitudeTask, elevationTask, headingTask);

        var latitude = latitudeTask.Result;
        var longitude = longitudeTask.Result;
        var elevation = elevationTask.Result;
        if (!IsValidLatitudeLongitude(latitude, longitude) || !elevation.HasValue)
        {
            return null;
        }

        var targetLatitude = latitude.GetValueOrDefault();
        var targetLongitude = longitude.GetValueOrDefault();
        var altitudeFt = elevation.Value * FeetPerMeter;
        var distanceNm = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, targetLatitude, targetLongitude);
        if (!IsTrackableTraffic(distanceNm, altitudeFt))
        {
            return null;
        }

        return new TrafficContactState(
            $"XP12-MP-{index}",
            null,
            targetLatitude,
            targetLongitude,
            altitudeFt,
            headingTask.Result.HasValue ? GeoMath.NormalizeDegrees(headingTask.Result.Value) : null,
            null,
            timestamp);
    }

    private IReadOnlyList<TrafficContactState> BuildDirectTcasTrafficContacts(
        OwnshipState ownship,
        DateTimeOffset timestamp,
        IReadOnlyList<double> modeSIds,
        IReadOnlyList<double> latitudesDeg,
        IReadOnlyList<double> longitudesDeg,
        IReadOnlyList<double> elevationsMeters,
        IReadOnlyList<double> tracksDeg,
        IReadOnlyList<double> headingsDeg,
        IReadOnlyList<double> speedsMetersPerSecond)
    {
        var count = new[]
        {
            latitudesDeg.Count,
            longitudesDeg.Count,
            elevationsMeters.Count
        }.Min();

        var contacts = new List<TrafficContactState>(Math.Max(0, count - 1));
        for (var i = 1; i < count; i++)
        {
            var latitude = latitudesDeg[i];
            var longitude = longitudesDeg[i];
            if (!IsValidLatitudeLongitude(latitude, longitude))
            {
                continue;
            }

            var distanceNm = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, latitude, longitude);
            var altitudeFt = elevationsMeters[i] * FeetPerMeter;
            if (!IsTrackableTraffic(distanceNm, altitudeFt))
            {
                continue;
            }

            contacts.Add(new TrafficContactState(
                BuildTcasId(modeSIds, i),
                null,
                latitude,
                longitude,
                altitudeFt,
                ReadHeading(tracksDeg, headingsDeg, i),
                ReadSpeed(speedsMetersPerSecond, i),
                timestamp));
        }

        return contacts;
    }

    private async Task<OwnshipState> ReadOwnshipAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var latitudeTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/latitude"], cancellationToken);
        var longitudeTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/longitude"], cancellationToken);
        var elevationTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/elevation"], cancellationToken);
        var headingTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/true_psi"], cancellationToken);
        var trackTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/hpath"], cancellationToken);
        var magneticHeadingTask = GetOptionalDoubleAsync("sim/flightmodel/position/mag_psi", cancellationToken);
        var groundspeedTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/groundspeed"], cancellationToken);

        await Task.WhenAll(latitudeTask, longitudeTask, elevationTask, headingTask, trackTask, magneticHeadingTask, groundspeedTask);

        var trueHeading = GeoMath.NormalizeDegrees(headingTask.Result);
        var magneticVariation = magneticHeadingTask.Result.HasValue
            ? GeoMath.SignedRelativeBearingDeg(GeoMath.NormalizeDegrees(magneticHeadingTask.Result.Value), trueHeading)
            : (double?)null;

        var ownship = new OwnshipState(
            "OWN",
            latitudeTask.Result,
            longitudeTask.Result,
            elevationTask.Result * FeetPerMeter,
            trueHeading,
            groundspeedTask.Result * KnotsPerMeterPerSecond,
            timestamp,
            magneticVariation,
            GeoMath.NormalizeDegrees(trackTask.Result));

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "ownship-sample",
            TimeSpan.FromSeconds(2),
            () => $"Ownship sample | lat={ownship.LatitudeDeg:F5} lon={ownship.LongitudeDeg:F5} altFt={ownship.AltitudeFt:F0} hdg={ownship.HeadingDeg:F1} gsKt={ownship.SpeedKt:F0}");

        return ownship;
    }

    private IReadOnlyList<TrafficContactState> BuildRelativeTcasTrafficContacts(
        OwnshipState ownship,
        DateTimeOffset timestamp,
        IReadOnlyList<double> modeSIds,
        IReadOnlyList<double> relativeBearingsDeg,
        IReadOnlyList<double> distancesMeters,
        IReadOnlyList<double> relativeAltitudesMeters,
        IReadOnlyList<double> tracksDeg,
        IReadOnlyList<double> headingsDeg,
        IReadOnlyList<double> speedsMetersPerSecond)
    {
        var count = new[]
        {
            relativeBearingsDeg.Count,
            distancesMeters.Count,
            relativeAltitudesMeters.Count
        }.Min();

        var contacts = new List<TrafficContactState>(Math.Max(0, count - 1));
        for (var i = 1; i < count; i++)
        {
            var distanceMeters = distancesMeters[i];
            if (distanceMeters <= 0)
            {
                continue;
            }

            var bearingTrue = GeoMath.NormalizeDegrees(ownship.HeadingDeg + relativeBearingsDeg[i]);
            var distanceNm = distanceMeters / MetersPerNauticalMile;
            var position = GeoMath.DestinationPoint(ownship.LatitudeDeg, ownship.LongitudeDeg, bearingTrue, distanceNm);
            var altitudeFt = ownship.AltitudeFt + (relativeAltitudesMeters[i] * FeetPerMeter);
            if (!IsTrackableTraffic(distanceNm, altitudeFt))
            {
                continue;
            }

            contacts.Add(new TrafficContactState(
                BuildTcasId(modeSIds, i),
                null,
                position.latitudeDeg,
                position.longitudeDeg,
                altitudeFt,
                ReadHeading(tracksDeg, headingsDeg, i),
                ReadSpeed(speedsMetersPerSecond, i),
                timestamp));
        }

        return contacts;
    }

    private async Task TryResolveOptionalDataRefAsync(string dataRefName, CancellationToken cancellationToken)
    {
        try
        {
            _dataRefIds[dataRefName] = await ResolveDataRefIdAsync(dataRefName, cancellationToken);
        }
        catch
        {
            // Optional fallbacks differ by X-Plane version and plugin traffic provider.
        }
    }

    private async Task DiscoverApiVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _webApi.DiscoverApiVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            DataSourceDebugLog.Info(LogSource, $"Capabilities endpoint request failed; falling back to X-Plane Web API v1 | error={ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DataSourceDebugLog.Info(LogSource, $"Capabilities endpoint timed out; falling back to X-Plane Web API v1 | error={ex.Message}");
        }
    }

    private Task<IReadOnlyList<double>> GetOptionalNumericArrayAsync(string dataRefName, CancellationToken cancellationToken)
    {
        return _dataRefIds.TryGetValue(dataRefName, out var dataRefId)
            ? GetNumericArrayAsync(dataRefId, cancellationToken)
            : Task.FromResult<IReadOnlyList<double>>([]);
    }

    private async Task<double?> GetOptionalDoubleAsync(string dataRefName, CancellationToken cancellationToken)
    {
        return _dataRefIds.TryGetValue(dataRefName, out var dataRefId)
            ? await GetDoubleAsync(dataRefId, cancellationToken)
            : null;
    }

    private async Task<long> ResolveDataRefIdAsync(string dataRefName, CancellationToken cancellationToken)
    {
        return await _webApi.ResolveDataRefIdAsync(dataRefName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<double> GetDoubleAsync(long dataRefId, CancellationToken cancellationToken)
    {
        using var document = await GetValueDocumentAsync(dataRefId, cancellationToken);
        return ReadNumericValue(document.RootElement.GetProperty("data"));
    }

    private async Task<IReadOnlyList<double>> GetNumericArrayAsync(long dataRefId, CancellationToken cancellationToken)
    {
        using var document = await GetValueDocumentAsync(dataRefId, cancellationToken);
        var value = document.RootElement.GetProperty("data");
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<double>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            values.Add(ReadNumericValue(item));
        }

        return values;
    }

    private async Task<JsonDocument> GetValueDocumentAsync(long dataRefId, CancellationToken cancellationToken)
    {
        return await _webApi.GetValueDocumentAsync(dataRefId, cancellationToken).ConfigureAwait(false);
    }

    private Uri GetBaseUri() => _webApi.BaseUri;

    private void SetConnected(bool value, bool forceNotify = false)
    {
        if (_isConnected == value && !forceNotify)
        {
            return;
        }

        _isConnected = value;
        DataSourceDebugLog.Info(LogSource, $"Connection state changed | connected={value}");
        ConnectionChanged?.Invoke(this, value);
    }

    private static double ReadNumericValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0
        };

    private bool IsTrackableTraffic(double distanceNm, double altitudeFt) =>
        distanceNm > 0 &&
        distanceNm <= _settings.SelectedRangeNm &&
        altitudeFt >= _settings.MinTrackedAltitudeFt;

    private static bool IsValidLatitudeLongitude(double? latitudeDeg, double? longitudeDeg) =>
        latitudeDeg.HasValue &&
        longitudeDeg.HasValue &&
        double.IsFinite(latitudeDeg.Value) &&
        double.IsFinite(longitudeDeg.Value) &&
        latitudeDeg.Value is >= -90 and <= 90 &&
        longitudeDeg.Value is >= -180 and <= 180 &&
        (Math.Abs(latitudeDeg.Value) > 0.000001 || Math.Abs(longitudeDeg.Value) > 0.000001);

    private static string BuildTcasId(IReadOnlyList<double> modeSIds, int index)
    {
        if (modeSIds.Count > index)
        {
            var modeSId = (long)Math.Round(modeSIds[index]);
            if (modeSId > 0)
            {
                return modeSId.ToString("X");
            }
        }

        return $"XP12-{index}";
    }

    private static double? ReadHeading(IReadOnlyList<double> tracksDeg, IReadOnlyList<double> headingsDeg, int index)
    {
        if (headingsDeg.Count > index && double.IsFinite(headingsDeg[index]))
        {
            return GeoMath.NormalizeDegrees(headingsDeg[index]);
        }

        if (tracksDeg.Count > index && double.IsFinite(tracksDeg[index]))
        {
            return GeoMath.NormalizeDegrees(tracksDeg[index]);
        }

        return null;
    }

    private static double? ReadSpeed(IReadOnlyList<double> speedsMetersPerSecond, int index) =>
        speedsMetersPerSecond.Count > index && double.IsFinite(speedsMetersPerSecond[index])
            ? speedsMetersPerSecond[index] * KnotsPerMeterPerSecond
            : null;
}
