using System.Net;
using System.Net.Http;
using System.Text;
using System.Collections.Concurrent;
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

    [Fact]
    public async Task Enrichment_OwnshipIdentityChangeRevokesPreviouslyPublishedOwnCallsign()
    {
        var inner = new ManualTrafficFeed();
        var settings = new TacticalDisplaySettings { VatsimDataFeedUrl = "https://unit.test/feed.json" };
        VatsimOwnshipIdentity identity = new("99999", "OTHER");
        var feed = new VatsimCallsignTrafficFeed(
            inner,
            settings,
            () => identity,
            new HttpClient(new JsonHandler("""
                {"general":{},"pilots":[{"callsign":"OWN2","latitude":60.1,"longitude":24,"altitude":5000,"groundspeed":250,"heading":180,"cid":23456}]}
                """)));
        var firstPublished = new TaskCompletionSource<TrafficSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublished = new TaskCompletionSource<TrafficSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCount = 0;
        feed.SnapshotReceived += (_, snapshot) =>
        {
            if (Interlocked.Increment(ref publishedCount) == 1) firstPublished.TrySetResult(snapshot);
            else secondPublished.TrySetResult(snapshot);
        };

        var now = DateTimeOffset.UtcNow;
        inner.Publish(Snapshot(now, null));
        var initial = await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OWN2", Assert.Single(initial.Contacts).Callsign);

        identity = new VatsimOwnshipIdentity("23456", null);
        inner.Publish(Snapshot(now.AddSeconds(1), null));
        var afterIdentityChange = await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contact = Assert.Single(afterIdentityChange.Contacts);
        Assert.Null(contact.Callsign);
        Assert.True(contact.CallsignRevoked);
        await feed.DisposeAsync();
    }

    [Fact]
    public async Task Enrichment_FormationBecomingAmbiguousRevokesUnsupportedAssignmentsAfterRetention()
    {
        var inner = new ManualTrafficFeed();
        var settings = new TacticalDisplaySettings { VatsimDataFeedUrl = "https://unit.test/feed.json" };
        var feed = new VatsimCallsignTrafficFeed(inner, settings, () => null,
            new HttpClient(new JsonHandler("""
                {"general":{},"pilots":[{"callsign":"RETRO61","latitude":60.1,"longitude":24,"altitude":5000,"groundspeed":250,"heading":90},{"callsign":"RETRO62","latitude":60.12,"longitude":24,"altitude":5000,"groundspeed":250,"heading":90}]}
                """)));
        var published = new ConcurrentQueue<TrafficSnapshot>();
        using var signal = new SemaphoreSlim(0);
        feed.SnapshotReceived += (_, snapshot) =>
        {
            published.Enqueue(snapshot);
            signal.Release();
        };

        var start = DateTimeOffset.UtcNow;
        inner.Publish(FormationSnapshot(start, 60.1, 60.12));
        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = published.Last();
        Assert.Equal("RETRO61", initial.Contacts[0].Callsign);
        Assert.Equal("RETRO62", initial.Contacts[1].Callsign);

        inner.Publish(FormationSnapshot(start.AddSeconds(1), 60.11, 60.11));
        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        var ambiguous = published.Last();
        Assert.All(ambiguous.Contacts, contact => Assert.Null(contact.Callsign));

        inner.Publish(FormationSnapshot(start.AddSeconds(16), 60.11, 60.11));
        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        var expired = published.Last();
        Assert.All(expired.Contacts, contact =>
        {
            Assert.Null(contact.Callsign);
            Assert.True(contact.CallsignRevoked);
        });
        Assert.All(published, snapshot => Assert.Equal(
            snapshot.Contacts.Where(contact => contact.Callsign is not null)
                .Select(contact => contact.Callsign!.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            snapshot.Contacts.Count(contact => contact.Callsign is not null)));
        await feed.DisposeAsync();
    }

    [Fact]
    public async Task Enrichment_DirectTacticalCallsignIsReservedBeforeVatsimConfirmation()
    {
        var inner = new ManualTrafficFeed();
        var settings = new TacticalDisplaySettings { VatsimDataFeedUrl = "https://unit.test/feed.json" };
        var feed = new VatsimCallsignTrafficFeed(inner, settings, () => null,
            new HttpClient(new JsonHandler("""{"general":{},"pilots":[{"callsign":"VIPER11","latitude":60.1,"longitude":24,"altitude":5000,"groundspeed":250,"heading":90,"cid":12345}]}""")));
        var received = new TaskCompletionSource<TrafficSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.SnapshotReceived += (_, snapshot) => received.TrySetResult(snapshot);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(new OwnshipState("OWN", 60, 24, 5000, 0, 250, now),
        [
            new TrafficContactState("sim:1", null, 60.1, 24, 5000, 90, 250, now),
            new TrafficContactState("tactical:random", "VIPER11", 60.1, 24, 5000, 90, 250, now, Source: TrackSource.TacticalLink)
        ], now);
        inner.Publish(snapshot);
        var published = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("VIPER11", Assert.Single(published.Contacts, contact => contact.Source == TrackSource.TacticalLink).Callsign);
        Assert.Null(Assert.Single(published.Contacts, contact => contact.Id == "sim:1").Callsign);
        await feed.DisposeAsync();
    }

    private static TrafficSnapshot Snapshot(DateTimeOffset timestamp, string? callsign) =>
        new(new OwnshipState("OWN", 60, 24, 5000, 90, 250, timestamp),
            [new TrafficContactState("T1", callsign, 60.1, 24, 5000, 180, 250, timestamp)], timestamp);

    private static TrafficSnapshot FormationSnapshot(DateTimeOffset timestamp, double firstLatitude, double secondLatitude) =>
        new(new OwnshipState("OWN", 60, 24, 5000, 0, 250, timestamp),
            [new TrafficContactState("T1", null, firstLatitude, 24, 5000, 90, 250, timestamp),
             new TrafficContactState("T2", null, secondLatitude, 24, 5000, 90, 250, timestamp)], timestamp);

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

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
