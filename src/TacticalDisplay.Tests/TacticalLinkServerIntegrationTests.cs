using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TacticalDisplay.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace TacticalDisplay.Tests;

public sealed class TacticalLinkServerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void ServerAcceptsCloudRs256IdentityAndRejectsTampering()
    {
        using var rsa = RSA.Create(2048);
        var originalPublicKey = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY");
        var originalIssuer = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER");
        var originalAudience = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE");
        var originalKeyId = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID");
        try
        {
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY", rsa.ExportSubjectPublicKeyInfoPem());
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER", "https://www.vtsd.app");
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE", "tactical-link");
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID", "test-key");
            var userId = Guid.NewGuid().ToString();
            var token = CreateToken(rsa, userId, "test-key");

            var identity = TacticalJwtValidator.Validate("Bearer " + token);
            Assert.Equal(userId, identity.UserId);
            Assert.Equal("VIPER11", identity.Callsign);
            Assert.Equal("1234567", identity.VatsimCid);
            Assert.Null(identity.AircraftType);

            var parts = token.Split('.');
            var tamperedSignature = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..];
            var tampered = parts[0] + "." + parts[1] + "." + tamperedSignature;
            Assert.Throws<UnauthorizedAccessException>(() => TacticalJwtValidator.Validate("Bearer " + tampered));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY", originalPublicKey);
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER", originalIssuer);
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE", originalAudience);
            Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID", originalKeyId);
        }
    }

    [Fact]
    public async Task AuthenticatedAircraftTypeControlsPublishedIdentityAndClientCannotGrantTankerCapability()
    {
        using var rsa = RSA.Create(2048);
        var oldKey = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY");
        var oldIssuer = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER");
        var oldAudience = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE");
        var oldKeyId = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID");
        try
        {
            ConfigureJwt(rsa);
            var identity = TacticalJwtValidator.Validate("Bearer " + CreateToken(rsa, Guid.NewGuid().ToString(), "test-key", "F35A"));
            var hub = new TacticalLinkHub(new PeerInterestResolver());
            using var aircraft = new TestPeer(hub, 60, 25, identity: identity);
            using var observer = new TestPeer(hub, 60.1, 25);
            await Task.WhenAll(aircraft.StartAsync(), observer.StartAsync());

            using var connected = await aircraft.ReadTypeAsync("CONNECTED");
            Assert.Equal("F35A", connected.RootElement.GetProperty("aircraftType").GetString());
            var ownCapabilities = connected.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("aar.receiver", ownCapabilities);
            Assert.DoesNotContain("aar.tanker", ownCapabilities);

            using var entered = await observer.ReadTypeAsync("PEER_ENTER");
            Assert.Equal("F35A", entered.RootElement.GetProperty("aircraftType").GetString());
            var peerCapabilities = entered.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("aar.receiver", peerCapabilities);
            Assert.DoesNotContain("aar.tanker", peerCapabilities);
            Assert.DoesNotContain("aircraftType", entered.RootElement.GetProperty("telemetry").EnumerateObject().Select(property => property.Name));

            await aircraft.SendAsync(new
            {
                type = "CAPABILITY_UPDATE",
                capabilities = new[] { "identity", "telemetry", "aar.tanker" },
                operationalStates = new { tankerAvailability = "Available" }
            });
            using var updated = await aircraft.ReadTypeAsync("CAPABILITY_UPDATED");
            var updatedCapabilities = updated.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("aar.receiver", updatedCapabilities);
            Assert.DoesNotContain("aar.tanker", updatedCapabilities);
            Assert.Empty(updated.RootElement.GetProperty("operationalStates").EnumerateObject());
        }
        finally { RestoreJwtEnvironment(oldKey, oldIssuer, oldAudience, oldKeyId); }
    }

    [Fact]
    public async Task AuthenticatedPilotWithoutAircraftTypeCanStillConnectWithoutAircraftCapabilities()
    {
        using var rsa = RSA.Create(2048);
        var oldKey = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY");
        var oldIssuer = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER");
        var oldAudience = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE");
        var oldKeyId = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID");
        try
        {
            ConfigureJwt(rsa);
            var identity = TacticalJwtValidator.Validate("Bearer " + CreateToken(rsa, Guid.NewGuid().ToString(), "test-key"));
            var hub = new TacticalLinkHub(new PeerInterestResolver());
            using var aircraft = new TestPeer(hub, 60, 25, identity: identity);
            await aircraft.StartAsync();
            using var connected = await aircraft.ReadTypeAsync("CONNECTED");
            Assert.Equal(JsonValueKind.Null, connected.RootElement.GetProperty("aircraftType").ValueKind);
            var capabilities = connected.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Contains("identity", capabilities);
            Assert.Contains("telemetry", capabilities);
            Assert.DoesNotContain("aar.receiver", capabilities);
            Assert.DoesNotContain("aar.tanker", capabilities);
        }
        finally { RestoreJwtEnvironment(oldKey, oldIssuer, oldAudience, oldKeyId); }
    }

    [Fact]
    public async Task LegacyCapabilityUpdateCannotSetTankerAvailabilityAndAuthRefreshClearsItAfterDowngrade()
    {
        using var rsa = RSA.Create(2048);
        var oldKey = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY");
        var oldIssuer = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER");
        var oldAudience = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE");
        var oldKeyId = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID");
        try
        {
            ConfigureJwt(rsa);
            var userId = Guid.NewGuid().ToString();
            var tankerIdentity = TacticalJwtValidator.Validate("Bearer " + CreateToken(rsa, userId, "test-key", "KC135"));
            var hub = new TacticalLinkHub(new PeerInterestResolver());
            using var tanker = new TestPeer(hub, 60, 25, identity: tankerIdentity);
            using var observer = new TestPeer(hub, 60.1, 25);
            await Task.WhenAll(tanker.StartAsync(), observer.StartAsync());
            using var tankerEntered = await observer.ReadTypeAsync("PEER_ENTER");
            var participantId = tankerEntered.RootElement.GetProperty("participantId").GetString();
            Assert.Equal("KC135", tankerEntered.RootElement.GetProperty("aircraftType").GetString());
            Assert.Contains("aar.tanker", tankerEntered.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));

            await tanker.SendAsync(new { type = "CAPABILITY_UPDATE", operationalStates = new { tankerAvailability = "Available" } });
            using var capabilityUpdate = await tanker.ReadTypeAsync("CAPABILITY_UPDATED");
            Assert.DoesNotContain(capabilityUpdate.RootElement.GetProperty("operationalStates").EnumerateObject(), property => property.Name == "tankerAvailability");

            var refreshedToken = CreateToken(rsa, userId, "test-key", "F35A");
            await tanker.SendAsync(new { type = "AUTH_REFRESH", token = refreshedToken });
            using var refreshed = await tanker.ReadTypeAsync("AUTH_REFRESHED");
            Assert.Equal("F35A", refreshed.RootElement.GetProperty("aircraftType").GetString());
            Assert.DoesNotContain("aar.tanker", refreshed.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
            Assert.Empty(refreshed.RootElement.GetProperty("operationalStates").EnumerateObject());

            using var peerUpdate = await observer.ReadTypeAsync("PEER_UPDATE");
            Assert.Equal(participantId, peerUpdate.RootElement.GetProperty("participantId").GetString());
            Assert.Equal("F35A", peerUpdate.RootElement.GetProperty("aircraftType").GetString());
            Assert.Contains("aar.receiver", peerUpdate.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain("aar.tanker", peerUpdate.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain(peerUpdate.RootElement.GetProperty("operationalStates").EnumerateObject(), property => property.Name == "tankerAvailability" && property.Value.GetString() == "Available");
        }
        finally { RestoreJwtEnvironment(oldKey, oldIssuer, oldAudience, oldKeyId); }
    }

    private static void ConfigureJwt(RSA rsa)
    {
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY", rsa.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER", "https://www.vtsd.app");
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE", "tactical-link");
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID", "test-key");
    }

    private static void RestoreJwtEnvironment(string? key, string? issuer, string? audience, string? keyId)
    {
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY", key);
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER", issuer);
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE", audience);
        Environment.SetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID", keyId);
    }

    [Fact]
    public async Task GlobalPresenceSendsNearbyPeersAndLeavesImmediatelyOnDisconnect()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        using var first = new TestPeer(hub, 60, 25);
        using var nearby = new TestPeer(hub, 60.1, 25);
        using var distant = new TestPeer(hub, 64, 25);
        await Task.WhenAll(first.StartAsync(), nearby.StartAsync(), distant.StartAsync());

        using (var message = await first.ReadTypeAsync("PEER_ENTER")) Assert.Equal("PEER_ENTER", message.RootElement.GetProperty("type").GetString());
        using (var message = await nearby.ReadTypeAsync("PEER_ENTER")) Assert.Equal("PEER_ENTER", message.RootElement.GetProperty("type").GetString());
        Assert.False(first.HasQueuedType("PEER_ENTER", distant.ParticipantId));

        await nearby.PublishFrameAsync(2);
        using (var message = await first.ReadTypeAsync("TELEMETRY"))
        {
            Assert.StartsWith("tl_", message.RootElement.GetProperty("participantId").GetString());
            Assert.Equal(2, message.RootElement.GetProperty("telemetry").GetProperty("sequence").GetInt64());
        }

        await nearby.SendAsync(new { type = "DISCONNECT" });
        using (var message = await first.ReadTypeAsync("PEER_LEAVE")) Assert.Equal("PEER_LEAVE", message.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReconnectRetainsPublicParticipantAndResetsSequenceForNewProcess()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var participantId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, participantId);
        await first.StartAsync();
        using var connectedFirst = await first.ReadTypeAsync("CONNECTED");
        var publicId = connectedFirst.RootElement.GetProperty("participantId").GetString();
        Assert.StartsWith("tl_", publicId);
        await first.PublishFrameAsync(20);
        await first.StopAbruptlyAsync();

        using var resumed = new TestPeer(hub, 60, 25, participantId);
        await resumed.StartAsync();
        using var connected = await resumed.ReadTypeAsync("CONNECTED");
        Assert.Equal(publicId, connected.RootElement.GetProperty("participantId").GetString());
        Assert.Equal(2, connected.RootElement.GetProperty("connectionGeneration").GetInt64());
        await resumed.PublishFrameAsync(1);
        await resumed.PublishFrameAsync(2);
    }

    [Fact]
    public async Task UnsupportedProtocolVersionGetsErrorAndClosesConnection()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        using var peer = new TestPeer(hub, 60, 25);
        await peer.StartAsync();
        await peer.SendRawAsync("{\"protocolVersion\":99,\"type\":\"PING\"}");
        using var error = await peer.ReadTypeAsync("ERROR");
        Assert.Equal("UNSUPPORTED_PROTOCOL_VERSION", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedMessageGetsExplicitError()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        using var peer = new TestPeer(hub, 60, 25);
        await peer.StartAsync();
        await peer.SendRawAsync("not-json");
        using var error = await peer.ReadTypeAsync("ERROR");
        Assert.Equal("INVALID_MESSAGE", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActivePresenceRetainsIdPastGraceAndAdvancesGeneration()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver(), reconnectGrace: TimeSpan.FromMilliseconds(50));
        var userId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, userId);
        await first.StartAsync();
        using var firstConnected = await first.ReadTypeAsync("CONNECTED");
        var id = firstConnected.RootElement.GetProperty("participantId").GetString();
        await Task.Delay(100);
        using var replacement = new TestPeer(hub, 60, 25, userId);
        await replacement.StartAsync();
        using var secondConnected = await replacement.ReadTypeAsync("CONNECTED");
        Assert.Equal(id, secondConnected.RootElement.GetProperty("participantId").GetString());
        Assert.Equal(2, secondConnected.RootElement.GetProperty("connectionGeneration").GetInt64());
        await first.StopAbruptlyAsync();
        await replacement.StopAsync();
    }

    [Fact]
    public async Task ConcurrentConnectionsForOneUserResolveToOnePublicPresence()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var userId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, userId);
        using var second = new TestPeer(hub, 60.1, 25, userId);
        await first.StartAsync();
        using var firstConnected = await first.ReadTypeAsync("CONNECTED");
        await second.StartAsync();
        using var secondConnected = await second.ReadTypeAsync("CONNECTED");
        Assert.Equal(firstConnected.RootElement.GetProperty("participantId").GetString(), secondConnected.RootElement.GetProperty("participantId").GetString());
        Assert.Equal(2, secondConnected.RootElement.GetProperty("connectionGeneration").GetInt64());
        Assert.Single(hub.GetConnectedParticipantIds());
        await Task.WhenAll(first.StopAbruptlyAsync(), second.StopAbruptlyAsync());
    }

    [Fact]
    public async Task SimultaneousConnectionsForOneUserAreSerializedToOnePublicPresence()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var userId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, userId);
        using var second = new TestPeer(hub, 60.1, 25, userId);
        var firstStart = first.StartAsync();
        var secondStart = second.StartAsync();
        await Task.WhenAll(firstStart, secondStart);
        var firstConnectedTask = first.ReadTypeAsync("CONNECTED");
        var secondConnectedTask = second.ReadTypeAsync("CONNECTED");
        var connections = await Task.WhenAll(firstConnectedTask, secondConnectedTask);
        Assert.Equal(connections[0].RootElement.GetProperty("participantId").GetString(), connections[1].RootElement.GetProperty("participantId").GetString());
        Assert.Equal(new long[] { 1, 2 }, connections.Select(message => message.RootElement.GetProperty("connectionGeneration").GetInt64()).Order());
        Assert.Single(hub.GetConnectedParticipantIds());
        foreach (var connection in connections) connection.Dispose();
        await Task.WhenAll(first.StopAbruptlyAsync(), second.StopAbruptlyAsync());
    }

    [Fact]
    public async Task GraceExpiryCleansAllParticipantStateAndOldGenerationCannotRemoveReplacement()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver(), reconnectGrace: TimeSpan.FromMilliseconds(120));
        var userId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, userId);
        await first.StartAsync();
        using var connected = await first.ReadTypeAsync("CONNECTED");
        var oldId = connected.RootElement.GetProperty("participantId").GetString()!;
        await first.PublishFrameAsync(2);
        await first.StopAbruptlyAsync();
        await Task.Delay(35);

        using var replacement = new TestPeer(hub, 60, 25, userId);
        await replacement.StartAsync();
        using var replacementConnected = await replacement.ReadTypeAsync("CONNECTED");
        var currentId = replacementConnected.RootElement.GetProperty("participantId").GetString()!;
        Assert.Equal(oldId, currentId);
        hub.ExpirePresence(userId, oldId, 1);
        Assert.Equal((1, 1, 1, 1), hub.GetLifecycleCounts());
        await replacement.StopAbruptlyAsync();
        await WaitForLifecycleCountsAsync(hub, (0, 0, 0, 0));

        using var fresh = new TestPeer(hub, 60, 25, userId);
        await fresh.StartAsync();
        using var freshConnected = await fresh.ReadTypeAsync("CONNECTED");
        Assert.NotEqual(oldId, freshConnected.RootElement.GetProperty("participantId").GetString());
        await fresh.StopAbruptlyAsync();
    }

    [Fact]
    public async Task ExplicitDisconnectImmediatelyReleasesPublicIdentityAndLimiters()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var userId = Guid.NewGuid().ToString();
        using var first = new TestPeer(hub, 60, 25, userId);
        await first.StartAsync();
        using var connected = await first.ReadTypeAsync("CONNECTED");
        var oldId = connected.RootElement.GetProperty("participantId").GetString();
        await first.SendAsync(new { type = "DISCONNECT" });
        await WaitForLifecycleCountsAsync(hub, (0, 0, 0, 0));
        using var fresh = new TestPeer(hub, 60, 25, userId);
        await fresh.StartAsync();
        using var freshConnected = await fresh.ReadTypeAsync("CONNECTED");
        Assert.NotEqual(oldId, freshConnected.RootElement.GetProperty("participantId").GetString());
        await fresh.StopAbruptlyAsync();
    }

    [Fact]
    public async Task PerPeerTelemetryAndControlRateLimitsAllowNormalRateAndClosePersistentAbuse()
    {
        var normalHub = new TacticalLinkHub(new PeerInterestResolver());
        using (var normal = new TestPeer(normalHub, 60, 25))
        {
            await normal.StartAsync();
            var connected = await normal.ReadTypeAsync("CONNECTED");
            var normalId = connected.RootElement.GetProperty("participantId").GetString()!;
            for (var index = 2; index <= 65; index++)
            {
                await normal.PublishFrameAsync(index);
                await Task.Delay(50);
            }
            Assert.Equal(0, normalHub.GetParticipantRateLimitEvents(normalId));
            await normal.StopAsync();
        }

        var abuseHub = new TacticalLinkHub(new PeerInterestResolver(), telemetryTokenLimit: 8, telemetryTokensPerSecond: 4, controlTokenLimit: 6, controlTokensPerSecond: 3);
        using var abusive = new TestPeer(abuseHub, 60, 25);
        using var observer = new TestPeer(abuseHub, 60.1, 25);
        await Task.WhenAll(abusive.StartAsync(), observer.StartAsync());
        var abusiveConnected = await abusive.ReadTypeAsync("CONNECTED");
        var observerConnected = await observer.ReadTypeAsync("CONNECTED");
        var abuseId = abusiveConnected.RootElement.GetProperty("participantId").GetString()!;
        var observerId = observerConnected.RootElement.GetProperty("participantId").GetString()!;
        for (var index = 2; index < 18; index++) await abusive.PublishFrameAsync(index);
        await Task.Delay(100);
        Assert.True(abuseHub.GetParticipantRateLimitEvents(abuseId) > 0);
        Assert.Equal(0, abuseHub.GetParticipantRateLimitEvents(observerId));

        for (var index = 0; index < 15; index++) await abusive.SendAsync(new { type = "PING" });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while (abuseHub.GetParticipantRateLimitEvents(abuseId) < 5)
                await Task.Delay(10, timeout.Token);
        Assert.True(abuseHub.GetParticipantRateLimitEvents(abuseId) >= 5);
        await Task.Delay(100);
        Assert.DoesNotContain(abuseId, abuseHub.GetConnectedParticipantIds());
        Assert.Contains(observerId, abuseHub.GetConnectedParticipantIds());
        await observer.StopAsync();
        await abusive.StopAbruptlyAsync();

        var controlHub = new TacticalLinkHub(new PeerInterestResolver(), controlTokenLimit: 5, controlTokensPerSecond: 1);
        using var controlAttacker = new TestPeer(controlHub, 60, 25);
        using var controlObserver = new TestPeer(controlHub, 60.1, 25);
        await Task.WhenAll(controlAttacker.StartAsync(), controlObserver.StartAsync());
        var attackerConnected = await controlAttacker.ReadTypeAsync("CONNECTED");
        var controlId = attackerConnected.RootElement.GetProperty("participantId").GetString()!;
        for (var index = 0; index < 12; index++) await controlAttacker.SendAsync(new { type = "PING" });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while (controlHub.GetParticipantRateLimitEvents(controlId) == 0)
                await Task.Delay(10, timeout.Token);
        Assert.True(controlHub.GetParticipantRateLimitEvents(controlId) > 0);
        await Task.Delay(100);
        Assert.DoesNotContain(controlId, controlHub.GetConnectedParticipantIds());
        await controlObserver.StopAsync();
        await controlAttacker.StopAbruptlyAsync();
    }

    [Fact]
    public async Task FiftyConcurrentPeersCanPublishTwentyFramesPerSecond()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var peers = Enumerable.Range(0, 50).Select(index => new TestPeer(hub,
            index < 25 ? 40 + (index % 5) * 0.01 : 60 + (index % 5) * 0.01,
            index < 25 ? -74 + (index / 5) * 0.01 : 25 + (index / 5) * 0.01)).ToArray();
        try
        {
            await Task.WhenAll(peers.Select(peer => peer.StartAsync()));
            var stopwatch = Stopwatch.StartNew();
            var start = Stopwatch.GetTimestamp();
            for (var frame = 1; frame <= 20; frame++)
            {
                await Task.WhenAll(peers.Select(peer => peer.PublishFrameAsync(frame + 1)));
                await WaitForNextFrameAsync(start, frame);
            }
            await Task.WhenAll(peers.Select(peer => peer.PingAndWaitAsync()));
            stopwatch.Stop();
            Assert.All(peers, peer => Assert.Equal(1, peer.PongCount));
            output.WriteLine($"TacticalLink load sample | peers=50 framesPerPeer=20 targetRateHz=20 elapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}");
        }
        finally
        {
            await Task.WhenAll(peers.Select(peer => peer.StopAsync()));
            foreach (var peer in peers) peer.Dispose();
        }
    }

    [Fact]
    public async Task OneHundredConcurrentPeersExchangeTwentyFramesPerSecond()
    {
        var hub = new TacticalLinkHub(new PeerInterestResolver());
        var peers = Enumerable.Range(0, 100).Select(index => new TestPeer(hub,
            index < 50 ? 40 + (index % 10) * 0.01 : 60 + (index % 10) * 0.01,
            index < 50 ? -74 + (index / 10) * 0.01 : 25 + (index / 10) * 0.01)).ToArray();
        try
        {
            await Task.WhenAll(peers.Select(peer => peer.StartAsync()));
            var stopwatch = Stopwatch.StartNew();
            var start = Stopwatch.GetTimestamp();
            for (var frame = 1; frame <= 20; frame++)
            {
                await Task.WhenAll(peers.Select(peer => peer.PublishFrameAsync(frame + 1)));
                await WaitForNextFrameAsync(start, frame);
            }
            await Task.WhenAll(peers.Select(peer => peer.PingAndWaitAsync()));
            stopwatch.Stop();
            Assert.All(peers, peer => Assert.Equal(1, peer.PongCount));
            output.WriteLine($"TacticalLink load sample | peers=100 framesPerPeer=20 targetRateHz=20 elapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}");
        }
        finally
        {
            await Task.WhenAll(peers.Select(peer => peer.StopAsync()));
            foreach (var peer in peers) peer.Dispose();
        }
    }

    private static async Task WaitForNextFrameAsync(long startTimestamp, int frameNumber)
    {
        var targetElapsed = TimeSpan.FromMilliseconds(frameNumber * 50);
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        if (elapsed < targetElapsed)
            await Task.Delay(targetElapsed - elapsed);
    }

    private static async Task WaitForLifecycleCountsAsync(TacticalLinkHub hub,
        (int PresenceCount, int PeerCount, int TelemetryLimiterCount, int ControlLimiterCount) expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (hub.GetLifecycleCounts() != expected)
        {
            try { await Task.Delay(10, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for lifecycle counts {expected}; actual counts: {hub.GetLifecycleCounts()}.");
            }
        }
    }

    private sealed class TestPeer : IDisposable
    {
        private readonly TacticalLinkHub _hub;
        private readonly double _latitude;
        private readonly double _longitude;
        private readonly CancellationTokenSource _cts = new();
        private readonly TestWebSocket _socket = new();
        private Task? _runTask;
        private readonly AuthenticatedParticipant? _identity;
        public TestPeer(TacticalLinkHub hub, double latitude, double longitude, string? participantId = null, AuthenticatedParticipant? identity = null)
        {
            _hub = hub;
            _latitude = latitude;
            _longitude = longitude;
            _identity = identity;
            ParticipantId = identity?.UserId ?? participantId ?? Guid.NewGuid().ToString();
        }
        public string ParticipantId { get; }
        public int PongCount { get; private set; }

        public async Task StartAsync()
        {
            var identity = _identity ?? new AuthenticatedParticipant(ParticipantId, "1234567", "TEST1", null, DateTimeOffset.UtcNow.AddMinutes(2));
            _runTask = _hub.RunPeerAsync(_socket, identity, _cts.Token);
            await SendAsync(new { type = "CONNECT", participantId = "ignored" });
            await Task.Delay(25);
            await PublishAsync(1);
        }

        public Task PublishFrameAsync(int sequence) => PublishAsync(sequence);

        public async Task PingAndWaitAsync()
        {
            await Task.Delay(1100);
            await SendAsync(new { type = "PING" });
            using var pong = await ReadTypeAsync("PONG");
            PongCount++;
        }

        private Task PublishAsync(int sequence) => SendAsync(new
        {
            type = "TELEMETRY",
            sequence,
            sampleTimestampUtc = DateTimeOffset.UtcNow,
            latitudeDeg = _latitude,
            longitudeDeg = _longitude,
            altitudeFt = 25000,
            headingDeg = 90,
            groundTrackDeg = 90,
            speedKt = 300,
            aircraftType = "KC135"
        });

        public Task SendAsync(object message)
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            payload["protocolVersion"] = JsonSerializer.SerializeToElement(1);
            return _socket.SendClientMessageAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        public Task SendRawAsync(string json) => _socket.SendClientMessageAsync(json);
        public async Task<JsonDocument> ReadTypeAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var json = await _socket.ReadServerMessageAsync(timeout.Token);
                var parsed = JsonDocument.Parse(json);
                if (parsed.RootElement.TryGetProperty("type", out var found) && found.GetString() == type) return parsed;
                parsed.Dispose();
            }
        }
        public bool HasQueuedType(string type, string participantId) => _socket.HasServerMessage(type, participantId);

        public async Task StopAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                try { await SendAsync(new { type = "DISCONNECT" }); } catch (ChannelClosedException) { }
            }
            if (_runTask is not null)
            {
                var finished = await Task.WhenAny(_runTask, Task.Delay(TimeSpan.FromSeconds(3)));
                if (finished != _runTask) _cts.Cancel();
                try { await _runTask; } catch (OperationCanceledException) { }
            }
        }
        public async Task StopAbruptlyAsync()
        {
            _cts.Cancel();
            if (_runTask is not null)
            {
                try { await _runTask; } catch (OperationCanceledException) { }
            }
        }
        public void Dispose() { _cts.Cancel(); _cts.Dispose(); _socket.Dispose(); }
    }

    private static string CreateToken(RSA rsa, string userId, string keyId, string? aircraftType = null)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = keyId }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = userId,
            user_id = userId,
            vatsim_cid = "1234567",
            callsign = "VIPER11",
            aircraft_type = aircraftType,
            iss = "https://www.vtsd.app",
            aud = "tactical-link",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nbf = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString()
        }));
        var signed = header + "." + payload;
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signed + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TestWebSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public async Task SendClientMessageAsync(string text) => await _incoming.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        public async Task<string> ReadServerMessageAsync(CancellationToken cancellationToken) => await _outgoing.Reader.ReadAsync(cancellationToken);
        public bool HasServerMessage(string type, string participantId)
        {
            while (_outgoing.Reader.TryRead(out var text))
            {
                using var message = JsonDocument.Parse(text);
                if (message.RootElement.GetProperty("type").GetString() == type &&
                    message.RootElement.TryGetProperty("participantId", out var id) && id.GetString() == participantId) return true;
            }
            return false;
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await _outgoing.Writer.WriteAsync("{\"type\":\"CLOSE\"}", cancellationToken);
            _state = WebSocketState.Closed;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryWrite([]);
            return Task.CompletedTask;
        }
        public override void Dispose() { _state = WebSocketState.Closed; _incoming.Writer.TryComplete(); }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await _incoming.Reader.ReadAsync(cancellationToken);
            if (bytes.Length == 0) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            await _outgoing.Writer.WriteAsync(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count), cancellationToken);
    }
}
