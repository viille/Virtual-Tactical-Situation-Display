using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class DemoTrafficFeed : ITrafficDataFeed
{
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _loopCts;
    private DateTimeOffset _lastUpdate = DateTimeOffset.UtcNow;
    private OwnshipTrack _ownship = new(60.3172, 24.9633, 23000, 045, 380);
    private readonly List<ContactTrack> _contacts =
    [
        new("A1", "VIPER11", 60.5200, 25.2400, 28000, 220, 430, 0),
        new("A2", "HAMMER21", 60.1800, 24.3200, 26500, 060, 360, 250),
        new("A3", "TANKER1", 60.6800, 24.8200, 22000, 170, 280, -100),
        new("A4", "GHOST31", 60.0500, 25.4200, 31000, 300, 440, 0)
    ];

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is not null)
        {
            return Task.CompletedTask;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastUpdate = DateTimeOffset.UtcNow;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        _ = Task.Run(() => LoopAsync(_loopCts.Token), CancellationToken.None);
        IsConnected = true;
        ConnectionChanged?.Invoke(this, true);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _loopCts?.Cancel();
        _timer?.Dispose();
        _loopCts = null;
        _timer = null;
        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(this, false);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        if (_timer is null)
        {
            return;
        }

        while (await _timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            var dtHours = System.Math.Max((now - _lastUpdate).TotalHours, 0.00001);
            _lastUpdate = now;

            _ownship = _ownship.Step(dtHours);
            var ownship = new OwnshipState(
                "OWN",
                _ownship.LatitudeDeg,
                _ownship.LongitudeDeg,
                _ownship.AltitudeFt,
                _ownship.HeadingDeg,
                _ownship.SpeedKt,
                now);

            var contacts = new List<TrafficContactState>(_contacts.Count);
            for (var i = 0; i < _contacts.Count; i++)
            {
                _contacts[i] = _contacts[i].Step(dtHours);
                var c = _contacts[i];
                contacts.Add(new TrafficContactState(
                    c.Id,
                    c.Callsign,
                    c.LatitudeDeg,
                    c.LongitudeDeg,
                    c.AltitudeFt,
                    c.HeadingDeg,
                    c.SpeedKt,
                    now));
            }

            SnapshotReceived?.Invoke(this, new TrafficSnapshot(ownship, contacts, now));
        }
    }

    private static (double lat, double lon) MoveByHeadingAndDistance(
        double latDeg,
        double lonDeg,
        double headingDeg,
        double distanceNm)
    {
        var rad = headingDeg * System.Math.PI / 180.0;
        var dLat = distanceNm * System.Math.Cos(rad) / 60.0;
        var cosLat = System.Math.Max(System.Math.Cos(latDeg * System.Math.PI / 180.0), 0.01);
        var dLon = distanceNm * System.Math.Sin(rad) / (60.0 * cosLat);
        return (latDeg + dLat, lonDeg + dLon);
    }

    private readonly record struct OwnshipTrack(
        double LatitudeDeg,
        double LongitudeDeg,
        double AltitudeFt,
        double HeadingDeg,
        double SpeedKt)
    {
        public OwnshipTrack Step(double dtHours)
        {
            var nm = SpeedKt * dtHours;
            var next = MoveByHeadingAndDistance(LatitudeDeg, LongitudeDeg, HeadingDeg, nm);
            return this with { LatitudeDeg = next.lat, LongitudeDeg = next.lon };
        }
    }

    private readonly record struct ContactTrack(
        string Id,
        string Callsign,
        double LatitudeDeg,
        double LongitudeDeg,
        double AltitudeFt,
        double HeadingDeg,
        double SpeedKt,
        double ClimbFtPerMin)
    {
        public ContactTrack Step(double dtHours)
        {
            var nm = SpeedKt * dtHours;
            var next = MoveByHeadingAndDistance(LatitudeDeg, LongitudeDeg, HeadingDeg, nm);
            var nextAlt = AltitudeFt + (ClimbFtPerMin * dtHours * 60.0);
            return this with
            {
                LatitudeDeg = next.lat,
                LongitudeDeg = next.lon,
                AltitudeFt = nextAlt
            };
        }
    }
}
