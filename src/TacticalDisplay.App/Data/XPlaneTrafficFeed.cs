using FSUIPC;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class XPlaneTrafficFeed : ITrafficDataFeed
{
    private const string LogSource = "XPlane";
    private readonly TacticalDisplaySettings _settings;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _loopCts;
    private bool _isRunning;
    private bool _isConnected;
    private DateTimeOffset _lastTrafficRefreshAt = DateTimeOffset.MinValue;
    private IReadOnlyList<TrafficContactState> _latestTraffic = [];

    public XPlaneTrafficFeed(TacticalDisplaySettings settings)
    {
        _settings = settings;
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
        CloseConnection();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                EnsureOpen();
                await PollLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DataSourceDebugLog.Info(LogSource, "Run loop canceled");
                break;
            }
            catch (FSUIPCException ex)
            {
                DataSourceDebugLog.Error(LogSource, "FSUIPC error in run loop", ex);
                CloseConnection();
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Error(LogSource, "Unhandled exception in run loop", ex);
                CloseConnection();
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var pollMs = (int)Math.Clamp(1000.0 / Math.Max(_settings.PollRateHz, 1), 100, 1000);
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(pollMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                EmitSnapshot();
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void EmitSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var ownship = ReadOwnship(now);
        RefreshTrafficIfDue(now);
        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "snapshot-summary",
            TimeSpan.FromSeconds(2),
            () => $"Snapshot emitted | trafficCount={_latestTraffic.Count}");
        SnapshotReceived?.Invoke(this, new TrafficSnapshot(ownship, _latestTraffic, now));
    }

    private OwnshipState ReadOwnship(DateTimeOffset now)
    {
        var snapshot = FSUIPCConnection.GetPositionSnapshot();
        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "ownship-sample",
            TimeSpan.FromSeconds(2),
            () => $"Ownship sample | lat={snapshot.Location.Latitude.DecimalDegrees:F5} lon={snapshot.Location.Longitude.DecimalDegrees:F5} altFt={snapshot.Altitude.Feet:F0} hdg={NormalizeDegrees(snapshot.HeadingDegreesTrue):F1} iasKt={snapshot.IndicatedAirspeedKnots:F0}");
        return new OwnshipState(
            "OWN",
            snapshot.Location.Latitude.DecimalDegrees,
            snapshot.Location.Longitude.DecimalDegrees,
            snapshot.Altitude.Feet,
            NormalizeDegrees(snapshot.HeadingDegreesTrue),
            snapshot.IndicatedAirspeedKnots,
            now);
    }

    private void RefreshTrafficIfDue(DateTimeOffset now)
    {
        if ((now - _lastTrafficRefreshAt).TotalMilliseconds < 500)
        {
            return;
        }

        FSUIPCConnection.AITrafficServices.RefreshAITrafficInformation();
        FSUIPCConnection.AITrafficServices.ApplyFilter(
            ApplyToGroundTraffic: true,
            ApplyToAirborneTraffic: true,
            StartBearing: 0,
            EndBearing: 360,
            MinAltitude: null,
            MaxAltitude: null,
            WithinDistance: _settings.SelectedRangeNm);

        _latestTraffic = FSUIPCConnection.AITrafficServices.AllTraffic
            .Select(plane => new TrafficContactState(
                plane.ID.ToString(),
                string.IsNullOrWhiteSpace(plane.ATCIdentifier) ? null : plane.ATCIdentifier.Trim(),
                plane.Location.Latitude.DecimalDegrees,
                plane.Location.Longitude.DecimalDegrees,
                plane.AltitudeFeet,
                NormalizeOptionalDegrees(plane.HeadingDegrees),
                plane.GroundSpeed,
                now))
            .ToList();

        DataSourceDebugLog.Debug(LogSource, $"Traffic refresh complete | count={_latestTraffic.Count} rangeNm={_settings.SelectedRangeNm}");

        _lastTrafficRefreshAt = now;
    }

    private void EnsureOpen()
    {
        if (FSUIPCConnection.IsOpen)
        {
            SetConnected(true);
            return;
        }

        DataSourceDebugLog.Info(LogSource, "Opening FSUIPC/XPUIPC connection");
        FSUIPCConnection.Open();
        SetConnected(true);
    }

    private void CloseConnection()
    {
        try
        {
            if (FSUIPCConnection.IsOpen)
            {
                DataSourceDebugLog.Info(LogSource, "Closing FSUIPC/XPUIPC connection");
                FSUIPCConnection.Close();
            }
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Error(LogSource, "Close connection failed", ex);
            // ignore close failures during reconnect/dispose
        }

        _latestTraffic = [];
        _lastTrafficRefreshAt = DateTimeOffset.MinValue;
        SetConnected(false);
    }

    private void SetConnected(bool value)
    {
        if (_isConnected == value)
        {
            return;
        }

        _isConnected = value;
        DataSourceDebugLog.Info(LogSource, $"Connection state changed | connected={value}");
        ConnectionChanged?.Invoke(this, value);
    }

    private static double NormalizeDegrees(double heading)
    {
        var normalized = heading % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double? NormalizeOptionalDegrees(double? heading) =>
        heading.HasValue ? NormalizeDegrees(heading.Value) : null;
}
