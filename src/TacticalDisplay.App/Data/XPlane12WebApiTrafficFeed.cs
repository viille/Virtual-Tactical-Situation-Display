using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class XPlane12WebApiTrafficFeed : ITrafficDataFeed
{
    private const string LogSource = "XPlane12";
    private const double MetersPerNauticalMile = 1852.0;
    private const double FeetPerMeter = 3.280839895;
    private const double KnotsPerMeterPerSecond = 1.9438444924406;

    private static readonly string[] RequiredOwnshipDataRefs =
    [
        "sim/flightmodel/position/latitude",
        "sim/flightmodel/position/longitude",
        "sim/flightmodel/position/elevation",
        "sim/flightmodel/position/true_psi",
        "sim/flightmodel/position/groundspeed"
    ];

    private static readonly string[] OptionalTrafficDataRefs =
    [
        "sim/cockpit2/tcas/targets/modeS_id",
        "sim/cockpit2/tcas/indicators/relative_bearing_degs",
        "sim/cockpit2/tcas/indicators/relative_distance_mtrs",
        "sim/cockpit2/tcas/indicators/relative_altitude_mtrs",
        "sim/cockpit2/tcas/targets/position/psi",
        "sim/cockpit2/tcas/targets/position/V_msc"
    ];

    private readonly TacticalDisplaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _loopCts;
    private bool _isRunning;
    private bool _isConnected;
    private string _apiVersion = "v1";
    private readonly Dictionary<string, long> _dataRefIds = new(StringComparer.Ordinal);

    public XPlane12WebApiTrafficFeed(TacticalDisplaySettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public bool IsConnected => _isConnected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        DataSourceDebugLog.Info(LogSource, $"Start requested | apiBaseUrl={GetBaseUri()} pollRateHz={_settings.PollRateHz:0.##}");
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
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
            catch (OperationCanceledException)
            {
                DataSourceDebugLog.Info(LogSource, "Run loop canceled");
                break;
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
        _apiVersion = await DiscoverApiVersionAsync(cancellationToken);
        _dataRefIds.Clear();

        foreach (var dataRefName in RequiredOwnshipDataRefs)
        {
            _dataRefIds[dataRefName] = await ResolveDataRefIdAsync(dataRefName, cancellationToken);
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

        DataSourceDebugLog.Info(LogSource, $"Resolved XP12 datarefs via {_apiVersion} API | trafficRefs={OptionalTrafficDataRefs.Count(_dataRefIds.ContainsKey)}/{OptionalTrafficDataRefs.Length}");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var pollMs = (int)Math.Clamp(1000.0 / Math.Max(_settings.PollRateHz, 1), 100, 1000);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken);
            SnapshotReceived?.Invoke(this, snapshot);
        }
    }

    private async Task<TrafficSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ownshipTask = ReadOwnshipAsync(now, cancellationToken);
        var modeSTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/modeS_id", cancellationToken);
        var bearingTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_bearing_degs", cancellationToken);
        var distanceTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_distance_mtrs", cancellationToken);
        var altitudeTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/indicators/relative_altitude_mtrs", cancellationToken);
        var headingTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/psi", cancellationToken);
        var speedTask = GetOptionalNumericArrayAsync("sim/cockpit2/tcas/targets/position/V_msc", cancellationToken);

        await Task.WhenAll(ownshipTask, modeSTask, bearingTask, distanceTask, altitudeTask, headingTask, speedTask);

        var ownship = ownshipTask.Result;
        var contacts = BuildTrafficContacts(
            ownship,
            now,
            modeSTask.Result,
            bearingTask.Result,
            distanceTask.Result,
            altitudeTask.Result,
            headingTask.Result,
            speedTask.Result);

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "snapshot-summary",
            TimeSpan.FromSeconds(2),
            () => $"Snapshot emitted | trafficCount={contacts.Count} apiVersion={_apiVersion}");

        return new TrafficSnapshot(ownship, contacts, now);
    }

    private async Task<OwnshipState> ReadOwnshipAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var latitudeTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/latitude"], cancellationToken);
        var longitudeTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/longitude"], cancellationToken);
        var elevationTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/elevation"], cancellationToken);
        var headingTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/true_psi"], cancellationToken);
        var groundspeedTask = GetDoubleAsync(_dataRefIds["sim/flightmodel/position/groundspeed"], cancellationToken);

        await Task.WhenAll(latitudeTask, longitudeTask, elevationTask, headingTask, groundspeedTask);

        var ownship = new OwnshipState(
            "OWN",
            latitudeTask.Result,
            longitudeTask.Result,
            elevationTask.Result * FeetPerMeter,
            GeoMath.NormalizeDegrees(headingTask.Result),
            groundspeedTask.Result * KnotsPerMeterPerSecond,
            timestamp);

        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "ownship-sample",
            TimeSpan.FromSeconds(2),
            () => $"Ownship sample | lat={ownship.LatitudeDeg:F5} lon={ownship.LongitudeDeg:F5} altFt={ownship.AltitudeFt:F0} hdg={ownship.HeadingDeg:F1} gsKt={ownship.SpeedKt:F0}");

        return ownship;
    }

    private IReadOnlyList<TrafficContactState> BuildTrafficContacts(
        OwnshipState ownship,
        DateTimeOffset timestamp,
        IReadOnlyList<double> modeSIds,
        IReadOnlyList<double> relativeBearingsDeg,
        IReadOnlyList<double> distancesMeters,
        IReadOnlyList<double> relativeAltitudesMeters,
        IReadOnlyList<double> headingsDeg,
        IReadOnlyList<double> speedsMetersPerSecond)
    {
        var count = new[]
        {
            modeSIds.Count,
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
            var heading = headingsDeg.Count > i ? GeoMath.NormalizeDegrees(headingsDeg[i]) : (double?)null;
            var speed = speedsMetersPerSecond.Count > i ? speedsMetersPerSecond[i] * KnotsPerMeterPerSecond : (double?)null;
            var modeSId = (long)Math.Round(modeSIds[i]);

            contacts.Add(new TrafficContactState(
                modeSId > 0 ? modeSId.ToString("X") : $"XP12-{i}",
                null,
                position.latitudeDeg,
                position.longitudeDeg,
                altitudeFt,
                heading,
                speed,
                timestamp));
        }

        return contacts;
    }

    private async Task<string> DiscoverApiVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(new Uri(GetBaseUri(), "api/capabilities"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            DataSourceDebugLog.Info(LogSource, "Capabilities endpoint unavailable; falling back to X-Plane Web API v1");
            return "v1";
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("X-Plane Web API rejected the connection. Check Network security policy and incoming traffic settings.");
        }

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("api", out var apiElement) ||
            !apiElement.TryGetProperty("versions", out var versionsElement) ||
            versionsElement.ValueKind != JsonValueKind.Array)
        {
            return "v1";
        }

        var versions = versionsElement
            .EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .OrderByDescending(ParseApiVersionNumber)
            .ToList();

        return versions.FirstOrDefault() ?? "v1";
    }

    private Task<IReadOnlyList<double>> GetOptionalNumericArrayAsync(string dataRefName, CancellationToken cancellationToken)
    {
        return _dataRefIds.TryGetValue(dataRefName, out var dataRefId)
            ? GetNumericArrayAsync(dataRefId, cancellationToken)
            : Task.FromResult<IReadOnlyList<double>>([]);
    }

    private async Task<long> ResolveDataRefIdAsync(string dataRefName, CancellationToken cancellationToken)
    {
        var relative = $"api/{_apiVersion}/datarefs?filter[name]={Uri.EscapeDataString(dataRefName)}";
        using var response = await _httpClient.GetAsync(new Uri(GetBaseUri(), relative), cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"X-Plane dataref '{dataRefName}' was not found.");
        }

        return data[0].GetProperty("id").GetInt64();
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
        var relative = $"api/{_apiVersion}/datarefs/{dataRefId}/value";
        using var response = await _httpClient.GetAsync(new Uri(GetBaseUri(), relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(json);
    }

    private Uri GetBaseUri()
    {
        var value = string.IsNullOrWhiteSpace(_settings.XPlane12ApiBaseUrl)
            ? "http://localhost:8086/"
            : _settings.XPlane12ApiBaseUrl.Trim();
        return value.EndsWith("/", StringComparison.Ordinal) ? new Uri(value) : new Uri($"{value}/");
    }

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

    private static int ParseApiVersionNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return 0;
        }

        return int.TryParse(version.TrimStart('v', 'V'), out var parsed) ? parsed : 0;
    }

    private static double ReadNumericValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0
        };
}
