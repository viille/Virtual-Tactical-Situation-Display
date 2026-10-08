using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using TacticalDisplay.App.Data;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class XPlane12WebApiTrafficFeedTests
{
    [Fact]
    public async Task PublishesFreshTenHertzPoseOnlyDuringAarSampling()
    {
        var handler = new FakeXpHandler();
        using var http = new HttpClient(handler);
        var settings = new TacticalDisplaySettings { PollRateHz = 2 };
        var feed = new XPlane12WebApiTrafficFeed(settings, http);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threePoses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new ConcurrentQueue<OwnshipState>();
        var snapshots = 0;
        feed.SnapshotReceived += (_, _) => Interlocked.Increment(ref snapshots);
        feed.ConnectionChanged += (_, isConnected) =>
        {
            if (isConnected) connected.TrySetResult();
        };
        feed.AarPoseSampled += (_, sample) =>
        {
            samples.Enqueue(sample);
            if (samples.Count >= 3) threePoses.TrySetResult();
        };

        try
        {
            await feed.StartAsync(CancellationToken.None);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(250);
            Assert.Empty(samples);

            feed.AarSamplingEnabled = true;
            await threePoses.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1100);
            var firstThree = samples.Take(3).ToArray();

            Assert.Equal(3, firstThree.Length);
            Assert.All(firstThree, sample =>
            {
                Assert.Equal(60, sample.LatitudeDeg);
                Assert.Equal(25, sample.LongitudeDeg);
                Assert.Equal(10000 * 3.280839895, sample.AltitudeFt, 4);
                Assert.Equal(45, sample.HeadingDeg);
                Assert.Equal(90, sample.GroundTrackDeg.GetValueOrDefault());
                Assert.Equal(100 * 1.9438444924406, sample.SpeedKt.GetValueOrDefault(), 4);
            });
            Assert.InRange(firstThree[2].Timestamp - firstThree[0].Timestamp, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.InRange(Volatile.Read(ref snapshots), 2, 5);
            Assert.InRange(handler.TcasValueReads, 20, 50);

            await feed.StopAsync();
            var countAtStop = samples.Count;
            await Task.Delay(250);
            Assert.Equal(countAtStop, samples.Count);
        }
        finally
        {
            await feed.DisposeAsync();
        }
    }

    private sealed class FakeXpHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, long> _ids = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, string> _names = new();
        private long _nextId;
        public int TcasValueReads => Volatile.Read(ref _tcasValueReads);
        private int _tcasValueReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/capabilities") return Task.FromResult(Json(HttpStatusCode.OK, "{\"api\":{\"versions\":[\"v1\"]}}"));
            if (path == "/api/v1/datarefs")
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var marker = "filter[name]=";
                var markerIndex = query.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0) return Task.FromResult(Json(HttpStatusCode.BadRequest, "{}"));
                var name = query[(markerIndex + marker.Length)..].Split('&')[0];
                var id = _ids.GetOrAdd(name, key =>
                {
                    var next = Interlocked.Increment(ref _nextId);
                    _names[next] = key;
                    return next;
                });
                return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"data\":[{{\"id\":{id}}}]}}"));
            }
            if (path.StartsWith("/api/v1/datarefs/", StringComparison.Ordinal) && path.EndsWith("/value", StringComparison.Ordinal))
            {
                var idText = path["/api/v1/datarefs/".Length..^"/value".Length];
                if (!long.TryParse(idText, out var id) || !_names.TryGetValue(id, out var name))
                    return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
                if (name.StartsWith("sim/cockpit2/tcas/", StringComparison.Ordinal)) Interlocked.Increment(ref _tcasValueReads);
                return Task.FromResult(Json(HttpStatusCode.OK, ValueFor(name)));
            }
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }

        private static string ValueFor(string name)
        {
            var value = name switch
            {
                "sim/flightmodel/position/latitude" => "60",
                "sim/flightmodel/position/longitude" => "25",
                "sim/flightmodel/position/elevation" => "10000",
                "sim/flightmodel/position/true_psi" => "45",
                "sim/flightmodel/position/hpath" => "90",
                "sim/flightmodel/position/groundspeed" => "100",
                "sim/flightmodel/position/mag_psi" => "20",
                _ when name.StartsWith("sim/multiplayer/position/", StringComparison.Ordinal) => "0",
                _ => "[]"
            };
            return $"{{\"data\":{value}}}";
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }
}
