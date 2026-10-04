using System.Net;
using System.Net.Http;
using System.Text;
using TacticalDisplay.App.Data;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class VatsimCallsignTrafficFeedTests
{
    [Fact]
    public async Task Enrichment_CoalescesPendingSnapshotsAndAppliesOwnshipGateBeforePublication()
    {
        var handler = new BlockingHandler();
        var inner = new ManualTrafficFeed();
        var settings = new TacticalDisplaySettings { VatsimDataFeedUrl = "https://unit.test/feed.json" };
        var feed = new VatsimCallsignTrafficFeed(
            inner,
            settings,
            () => new VatsimOwnshipIdentity("12345", null),
            new HttpClient(handler));
        var published = new List<TrafficSnapshot>();
        var received = new TaskCompletionSource<TrafficSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.SnapshotReceived += (_, snapshot) =>
        {
            lock (published) published.Add(snapshot);
            received.TrySetResult(snapshot);
        };

        var start = DateTimeOffset.UtcNow;
        inner.Publish(Snapshot(start, "FIRST1"));
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inner.Publish(Snapshot(start.AddSeconds(1), "SECOND1"));
        inner.Publish(Snapshot(start.AddSeconds(2), "OWN1"));
        handler.Release(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"general\":{},\"pilots\":[{\"callsign\":\"OWN1\",\"latitude\":60.1,\"longitude\":24,\"altitude\":5000,\"groundspeed\":250,\"heading\":180,\"cid\":12345}]}",
                Encoding.UTF8,
                "application/json")
        });

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(start.AddSeconds(2), result.Timestamp);
        Assert.Null(Assert.Single(result.Contacts).Callsign);
        lock (published) Assert.Single(published);
        await feed.DisposeAsync();
    }

    private static TrafficSnapshot Snapshot(DateTimeOffset timestamp, string callsign) =>
        new(new OwnshipState("OWN", 60, 24, 5000, 90, 250, timestamp),
            [new TrafficContactState("T1", callsign, 60.1, 24, 5000, 180, 250, timestamp)], timestamp);

    private sealed class ManualTrafficFeed : ITrafficDataFeed
    {
        public event EventHandler<TrafficSnapshot>? SnapshotReceived;
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public bool IsConnected => true;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(TrafficSnapshot snapshot) => SnapshotReceived?.Invoke(this, snapshot);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(HttpResponseMessage response) => _response.TrySetResult(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            return await _response.Task.WaitAsync(cancellationToken);
        }
    }
}
