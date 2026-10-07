using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("TACTICAL_LINK_LISTEN_URL") ?? "http://0.0.0.0:8080");
builder.Services.AddSingleton<IPeerInterestResolver, PeerInterestResolver>();
builder.Services.AddSingleton<TacticalLinkHub>();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
app.Map("/v1", async (HttpContext context, TacticalLinkHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    try
    {
        var identity = TacticalJwtValidator.Validate(context.Request.Headers.Authorization.ToString());
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await hub.RunPeerAsync(socket, identity, context.RequestAborted);
    }
    catch (UnauthorizedAccessException)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
});
app.Run();

public sealed record AuthenticatedParticipant(string UserId, string VatsimCid, string Callsign, string? AircraftType, DateTimeOffset ExpiresAt);
public sealed record ParticipantIdentity(string ParticipantId, string UserId, string VatsimCid, string Callsign, string? AircraftType, DateTimeOffset ExpiresAt);

public static class TacticalJwtValidator
{
    public static AuthenticatedParticipant Validate(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        var token = authorization[7..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3) throw new UnauthorizedAccessException();
        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var root = payload.RootElement;
            var expectedIssuer = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_ISSUER") ?? "https://www.vtsd.app";
            var expectedAudience = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_AUDIENCE") ?? "tactical-link";
            var expectedKeyId = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_KEY_ID");
            if (header.RootElement.GetProperty("alg").GetString() != "RS256" ||
                (expectedKeyId is { Length: > 0 } && header.RootElement.GetProperty("kid").GetString() != expectedKeyId) ||
                root.GetProperty("iss").GetString() != expectedIssuer ||
                !AudienceContains(root.GetProperty("aud"), expectedAudience) ||
                root.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new UnauthorizedAccessException();
            if (root.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10) throw new UnauthorizedAccessException();

            var pem = Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY")?.Replace("\\n", "\n");
            if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("TACTICAL_LINK_JWT_PUBLIC_KEY is required.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signed, Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new UnauthorizedAccessException();

            string Claim(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            var userId = Claim("user_id");
            var cid = Claim("vatsim_cid");
            var callsign = Claim("callsign");
            string? aircraftType = null;
            if (root.TryGetProperty("aircraft_type", out var aircraftTypeClaim) && aircraftTypeClaim.ValueKind != JsonValueKind.Null)
            {
                if (aircraftTypeClaim.ValueKind != JsonValueKind.String) throw new UnauthorizedAccessException();
                aircraftType = aircraftTypeClaim.GetString()?.Trim().ToUpperInvariant();
                if (aircraftType is { Length: > 0 } && (aircraftType.Length > 12 || aircraftType.Length < 2 || aircraftType.Any(character => !char.IsAsciiLetterOrDigit(character))))
                    throw new UnauthorizedAccessException();
                if (string.IsNullOrEmpty(aircraftType)) aircraftType = null;
            }
            if (string.IsNullOrWhiteSpace(userId) || Claim("sub") != userId ||
                string.IsNullOrWhiteSpace(cid) ||
                string.IsNullOrWhiteSpace(callsign) || callsign.Length > 16)
                throw new UnauthorizedAccessException();
            return new(userId, cid, callsign, aircraftType, DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()));
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            if (ex is InvalidOperationException && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TACTICAL_LINK_JWT_PUBLIC_KEY"))) throw;
            throw new UnauthorizedAccessException("Invalid TacticalLink token.", ex);
        }
    }

    private static bool AudienceContains(JsonElement audience, string expected) => audience.ValueKind switch
    {
        JsonValueKind.String => audience.GetString() == expected,
        JsonValueKind.Array => audience.EnumerateArray().Any(value => value.GetString() == expected),
        _ => false
    };
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

public sealed class TacticalLinkHub
{
    private static readonly IAircraftCapabilityResolver AircraftCapabilities = new StaticAircraftCapabilityResolver();
    private static readonly HashSet<string> AllowedTankerStates = ["Off", "Available", "Busy", "Unavailable"];
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PresenceIdentity> _presences = new(StringComparer.Ordinal);
    private readonly object _presenceLock = new();
    private readonly IPeerInterestResolver _interestResolver;
    private readonly TimeSpan _reconnectGrace;
    private readonly double _defaultRadiusNm = ReadRadius();
    private readonly double _maxRadiusNm = ReadMaxRadius();
    private readonly TimeSpan _telemetryStaleAfter = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _interestLock = new(1, 1);
    private int _interestDirty;
    private long _telemetryFramesIn, _telemetryFramesOut, _droppedTelemetryFrames, _rateLimitEvents, _reconnects, _authRefreshFailures;
    private int _nearbyPeerRelations;
    private readonly TimeProvider _timeProvider;
    private readonly int _telemetryTokenLimit;
    private readonly int _telemetryTokensPerSecond;
    private readonly int _controlTokenLimit;
    private readonly int _controlTokensPerSecond;
    private readonly ConcurrentDictionary<string, System.Threading.RateLimiting.TokenBucketRateLimiter> _telemetryLimiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, System.Threading.RateLimiting.TokenBucketRateLimiter> _controlLimiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _rateLimitEventsByParticipant = new(StringComparer.Ordinal);
    public TacticalLinkHub(IPeerInterestResolver interestResolver, TimeSpan? reconnectGrace = null, TimeProvider? timeProvider = null,
        int telemetryTokenLimit = 50, int telemetryTokensPerSecond = 45, int controlTokenLimit = 20, int controlTokensPerSecond = 15)
    {
        _interestResolver = interestResolver;
        _reconnectGrace = reconnectGrace ?? TimeSpan.FromSeconds(12);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetryTokenLimit = telemetryTokenLimit;
        _telemetryTokensPerSecond = telemetryTokensPerSecond;
        _controlTokenLimit = controlTokenLimit;
        _controlTokensPerSecond = controlTokensPerSecond;
        _ = Task.Run(InterestRefreshLoopAsync);
        _ = Task.Run(TelemetryExpiryLoopAsync);
        _ = Task.Run(MetricsLogLoopAsync);
    }

    public async Task RunPeerAsync(WebSocket socket, AuthenticatedParticipant identity, CancellationToken cancellationToken)
    {
        var (publicId, generation, peer, replaced) = Activate(identity, socket);
        replaced?.CancelGrace();
        replaced?.TryClose(WebSocketCloseStatus.EndpointUnavailable, "reconnected");
        if (generation > 1) Interlocked.Increment(ref _reconnects);
        if (replaced is not null) replaced.Dispose();
        peer.InterestRadiusNm = _defaultRadiusNm;
        peer.StartWriter(cancellationToken);
        peer.TrySend(new { type = "CONNECTED", participantId = peer.Identity.ParticipantId, callsign = peer.Identity.Callsign, aircraftType = peer.Identity.AircraftType, capabilities = peer.Capabilities, operationalStates = peer.OperationalStates, connectionGeneration = generation });
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var lifetime = peer.Identity.ExpiresAt - DateTimeOffset.UtcNow;
                    if (lifetime <= TimeSpan.Zero)
                    {
                        peer.TryClose(WebSocketCloseStatus.PolicyViolation, "authentication expired");
                        return;
                    }
                    await Task.Delay(lifetime < TimeSpan.FromSeconds(10) ? lifetime : TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }, CancellationToken.None);
        await RefreshInterestAsync(cancellationToken);

        try
        {
            var buffer = new byte[32 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        peer.TrySend(new { type = "ERROR", code = "INVALID_MESSAGE", message = "Text protocol required." });
                        peer.TryClose(WebSocketCloseStatus.InvalidMessageType, "text protocol required");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 128 * 1024)
                    {
                        peer.TrySend(new { type = "ERROR", code = "INVALID_MESSAGE", message = "Message size limit exceeded." });
                        peer.TryClose(WebSocketCloseStatus.MessageTooBig, "message limit exceeded");
                        return;
                    }
                } while (!result.EndOfMessage);
                try { await HandleMessageAsync(peer, generation, Encoding.UTF8.GetString(message.ToArray()), cancellationToken); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
                {
                    peer.TrySend(new { type = "ERROR", code = "INVALID_MESSAGE", message = "Malformed TacticalLink message." });
                }
                if (peer.IsSlow) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            if (_peers.TryGetValue(publicId, out var current) && current.Generation == generation)
            {
                current.MarkDisconnected();
                lock (_presenceLock)
                {
                    if (_presences.TryGetValue(identity.UserId, out var presence) && presence.ParticipantId == publicId && presence.Generation == generation)
                        _presences[identity.UserId] = presence with { Connected = false, GraceExpiresAt = current.ExplicitlyDisconnected ? null : _timeProvider.GetUtcNow() + _reconnectGrace };
                }
                if (current.ExplicitlyDisconnected) ExpirePresence(identity.UserId, publicId, generation);
                else current.StartGrace(_reconnectGrace, _timeProvider, () => ExpirePresence(identity.UserId, publicId, generation));
            }
        }
    }

    private async Task HandleMessageAsync(PeerConnection peer, long generation, string json, CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(peer, generation)) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
        {
            peer.TrySend(new { type = "ERROR", code = "INVALID_MESSAGE", message = "A message type is required." });
            return;
        }
        if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != TacticalLinkProtocol.Version)
        {
            peer.TrySend(new { type = "ERROR", code = "UNSUPPORTED_PROTOCOL_VERSION", message = "Unsupported TacticalLink protocol version." });
            peer.TryClose(WebSocketCloseStatus.ProtocolError, "unsupported protocol version");
            return;
        }
        var type = typeNode.GetString();
        var isTelemetryMessage = type == "TELEMETRY";
        var limiters = isTelemetryMessage ? _telemetryLimiters : _controlLimiters;
        var limiter = limiters.GetOrAdd(peer.Identity.ParticipantId, _ => new System.Threading.RateLimiting.TokenBucketRateLimiter(new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = isTelemetryMessage ? _telemetryTokenLimit : _controlTokenLimit,
            TokensPerPeriod = isTelemetryMessage ? _telemetryTokensPerSecond : _controlTokensPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
        }));
        using var permit = limiter.AttemptAcquire();
        if (!permit.IsAcquired)
        {
            Interlocked.Increment(ref _rateLimitEvents);
            _rateLimitEventsByParticipant.AddOrUpdate(peer.Identity.ParticipantId, 1, static (_, count) => count + 1);
            if (isTelemetryMessage) Interlocked.Increment(ref _droppedTelemetryFrames);
            peer.RateLimitViolations++;
            if (peer.RateLimitViolations >= 5) peer.TryClose(WebSocketCloseStatus.PolicyViolation, "rate limit exceeded");
            return;
        }
        peer.RateLimitViolations = System.Math.Max(0, peer.RateLimitViolations - 1);
        switch (type)
        {
            case "CONNECT":
                peer.IsConnected = true;
                peer.UpdateOperationalStates(root);
                await RefreshInterestAsync(cancellationToken);
                break;
            case "DISCONNECT":
                peer.IsConnected = false;
                peer.InReconnectGrace = false;
                peer.ExplicitlyDisconnected = true;
                await RefreshInterestAsync(cancellationToken);
                peer.TryClose(WebSocketCloseStatus.NormalClosure, "disconnected");
                break;
            case "TELEMETRY":
                var telemetry = JsonSerializer.Deserialize<TacticalTelemetry>(root, JsonOptions);
                if (telemetry is null || !IsValidTelemetry(telemetry) || telemetry.Sequence <= peer.LastSequence ||
                    telemetry.SampleTimestampUtc > DateTimeOffset.UtcNow.AddSeconds(5) || telemetry.SampleTimestampUtc < DateTimeOffset.UtcNow.AddSeconds(-10)) return;
                peer.LastSequence = telemetry.Sequence;
                Interlocked.Increment(ref _telemetryFramesIn);
                peer.Telemetry = telemetry;
                peer.LastTelemetryReceivedAt = DateTimeOffset.UtcNow;
                if (!IsCurrentGeneration(peer, generation)) return;
                ScheduleInterestRefresh();
                await BroadcastTelemetryAsync(peer, cancellationToken);
                break;
            case "INTEREST_UPDATE":
                var requested = root.TryGetProperty("radiusNm", out var radius) && radius.ValueKind == JsonValueKind.Number ? radius.GetDouble() : _defaultRadiusNm;
                if (!double.IsFinite(requested) || requested is < 1 or > 1000)
                {
                    peer.TrySend(new { type = "ERROR", code = "INVALID_MESSAGE", message = "Invalid interest radius." });
                    break;
                }
                peer.InterestRadiusNm = System.Math.Clamp(requested, 1, _maxRadiusNm);
                await RefreshInterestAsync(cancellationToken);
                break;
            case "CAPABILITY_UPDATE":
                peer.UpdateOperationalStates(root);
                peer.TrySend(new { type = "CAPABILITY_UPDATED", capabilities = peer.Capabilities, operationalStates = peer.OperationalStates });
                await RefreshInterestAsync(cancellationToken);
                break;
            case "AUTH_REFRESH":
                if (root.TryGetProperty("token", out var refreshToken) && refreshToken.GetString() is { Length: > 0 } jwt)
                {
                    try
                    {
                        var refreshed = TacticalJwtValidator.Validate("Bearer " + jwt);
                        if (refreshed.UserId == peer.Identity.UserId && refreshed.VatsimCid == peer.Identity.VatsimCid)
                        {
                            peer.UpdateIdentity(new ParticipantIdentity(peer.Identity.ParticipantId, refreshed.UserId, refreshed.VatsimCid, refreshed.Callsign, refreshed.AircraftType, refreshed.ExpiresAt));
                            peer.TrySend(new { type = "AUTH_REFRESHED", callsign = refreshed.Callsign, aircraftType = refreshed.AircraftType, capabilities = peer.Capabilities, operationalStates = peer.OperationalStates, expiresAt = refreshed.ExpiresAt });
                            await RefreshInterestAsync(cancellationToken);
                        }
                        else peer.TrySend(new { type = "ERROR", code = "IDENTITY_MISMATCH", message = "The refreshed identity did not match this connection." });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        peer.TrySend(new { type = "ERROR", code = "AUTH_REFRESH_REJECTED", message = "The refreshed identity is invalid." });
                    }
                }
                break;
            case "PING": peer.TrySend(new { type = "PONG", timestampUtc = DateTimeOffset.UtcNow }); break;
            default: peer.TrySend(new { type = "ERROR", code = "UNKNOWN_MESSAGE", message = "Unsupported TacticalLink message." }); break;
        }
    }

    private bool IsCurrentGeneration(PeerConnection peer, long generation) =>
        peer.Generation == generation && _peers.TryGetValue(peer.Identity.ParticipantId, out var current) && ReferenceEquals(current, peer);

    private (string ParticipantId, long Generation, PeerConnection Peer, PeerConnection? Replaced) Activate(AuthenticatedParticipant identity, WebSocket socket)
    {
        lock (_presenceLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_presences.TryGetValue(identity.UserId, out var existing))
            {
                if (existing.Connected || existing.GraceExpiresAt > now)
                {
                    var generation = existing.Generation + 1;
                    var replaced = _peers.TryGetValue(existing.ParticipantId, out var peer) ? peer : null;
                    _presences[identity.UserId] = existing with { Generation = generation, Connected = true, GraceExpiresAt = null };
                    var authenticatedIdentity = ToParticipantIdentity(existing.ParticipantId, identity);
                    var current = replaced?.Resume(authenticatedIdentity, generation, socket) ?? new PeerConnection(authenticatedIdentity, generation, socket);
                    _peers[existing.ParticipantId] = current;
                    return (existing.ParticipantId, generation, current, replaced);
                }
                ExpirePresenceUnderLock(identity.UserId, existing.ParticipantId, existing.Generation);
            }

            var participantId = "tl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _presences[identity.UserId] = new PresenceIdentity(participantId, 1, true, null);
            var newPeer = new PeerConnection(ToParticipantIdentity(participantId, identity), 1, socket);
            _peers[participantId] = newPeer;
            return (participantId, 1, newPeer, null);
        }
    }

    private static ParticipantIdentity ToParticipantIdentity(string participantId, AuthenticatedParticipant identity) =>
        new(participantId, identity.UserId, identity.VatsimCid, identity.Callsign, identity.AircraftType, identity.ExpiresAt);

    internal void ExpirePresence(string userId, string participantId, long generation)
    {
        lock (_presenceLock) ExpirePresenceUnderLock(userId, participantId, generation);
        _ = RefreshInterestAsync(CancellationToken.None);
    }

    private void ExpirePresenceUnderLock(string userId, string participantId, long generation)
    {
        if (!_presences.TryGetValue(userId, out var presence) || presence.ParticipantId != participantId || presence.Generation != generation || presence.Connected)
            return;
        if (presence.GraceExpiresAt is { } expiry && expiry > _timeProvider.GetUtcNow())
        {
            if (_peers.TryGetValue(participantId, out var pendingPeer) && pendingPeer.Generation == generation)
                pendingPeer.StartGrace(expiry - _timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(20), _timeProvider,
                    () => ExpirePresence(userId, participantId, generation));
            return;
        }
        _presences.TryRemove(userId, out _);
        if (_peers.TryGetValue(participantId, out var peer) && peer.Generation == generation && _peers.TryRemove(participantId, out var removed))
            removed.Dispose();
        _telemetryLimiters.TryRemove(participantId, out var telemetryLimiter);
        _controlLimiters.TryRemove(participantId, out var controlLimiter);
        telemetryLimiter?.Dispose();
        controlLimiter?.Dispose();
        _rateLimitEventsByParticipant.TryRemove(participantId, out _);
    }

    public (int PresenceCount, int PeerCount, int TelemetryLimiterCount, int ControlLimiterCount) GetLifecycleCounts() =>
        (_presences.Count, _peers.Count, _telemetryLimiters.Count, _controlLimiters.Count);

    public IReadOnlySet<string> GetConnectedParticipantIds() => _peers.Values.Where(peer => peer.IsConnected)
        .Select(peer => peer.Identity.ParticipantId).ToHashSet(StringComparer.Ordinal);

    public long GetParticipantRateLimitEvents(string participantId) => _rateLimitEventsByParticipant.GetValueOrDefault(participantId);

    private sealed record PresenceIdentity(string ParticipantId, long Generation, bool Connected, DateTimeOffset? GraceExpiresAt);

    private async Task RefreshInterestAsync(CancellationToken cancellationToken)
    {
        await _interestLock.WaitAsync(cancellationToken);
        try
        {
            var connected = _peers.Values.Where(peer => (peer.IsConnected || peer.InReconnectGrace) && peer.Telemetry is not null).ToArray();
            var positions = connected.ToDictionary(peer => peer.Identity.ParticipantId, peer => peer.Telemetry!, StringComparer.Ordinal);
            foreach (var recipient in _peers.Values.Where(peer => peer.IsConnected))
            {
                var nearby = recipient.Telemetry is null ? new HashSet<string>() : _interestResolver.Resolve(recipient.Identity.ParticipantId, positions, recipient.InterestRadiusNm).ToHashSet(StringComparer.Ordinal);
                var entered = nearby.Except(recipient.NearbyParticipantIds, StringComparer.Ordinal).ToArray();
                var left = recipient.NearbyParticipantIds.Except(nearby, StringComparer.Ordinal).ToArray();
                foreach (var id in left)
                {
                    recipient.RemoveTelemetry(id);
                    recipient.TrySend(new { type = "PEER_LEAVE", participantId = id });
                    recipient.PeerIdentityVersions.Remove(id);
                }
                foreach (var id in entered)
                    if (_peers.TryGetValue(id, out var other))
                    {
                        recipient.TrySend(PeerEvent("PEER_ENTER", other));
                        recipient.PeerIdentityVersions[id] = IdentityVersion(other);
                    }
                foreach (var id in nearby.Intersect(recipient.NearbyParticipantIds, StringComparer.Ordinal))
                {
                    if (_peers.TryGetValue(id, out var other) && recipient.PeerIdentityVersions.GetValueOrDefault(id) != IdentityVersion(other))
                    {
                        recipient.TrySend(PeerEvent("PEER_UPDATE", other));
                        recipient.PeerIdentityVersions[id] = IdentityVersion(other);
                    }
                }
                recipient.NearbyParticipantIds = nearby;
                Interlocked.Exchange(ref _nearbyPeerRelations, _peers.Values.Sum(value => value.NearbyParticipantIds.Count));
                if (recipient.IsSlow) recipient.TryClose(WebSocketCloseStatus.PolicyViolation, "slow client");
            }
        }
        finally { _interestLock.Release(); }
    }

    private void ScheduleInterestRefresh() => Interlocked.Exchange(ref _interestDirty, 1);

    private async Task InterestRefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync())
        {
            if (Interlocked.Exchange(ref _interestDirty, 0) == 0) continue;
            await RefreshInterestAsync(CancellationToken.None);
        }
    }

    private async Task TelemetryExpiryLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync())
        {
            var now = DateTimeOffset.UtcNow;
            var expired = false;
            foreach (var peer in _peers.Values)
            {
                if (peer.Telemetry is not null && now - peer.LastTelemetryReceivedAt > _telemetryStaleAfter)
                {
                    peer.Telemetry = null;
                    expired = true;
                }
            }
            if (expired) await RefreshInterestAsync(CancellationToken.None);
        }
    }

    private async Task MetricsLogLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync())
            Console.WriteLine(JsonSerializer.Serialize(new { eventName = "tactical_link_metrics", activeConnections = _peers.Values.Count(peer => peer.IsConnected), nearbyPeerRelations = Volatile.Read(ref _nearbyPeerRelations), telemetryFramesIn = Interlocked.Read(ref _telemetryFramesIn), telemetryFramesOut = Interlocked.Read(ref _telemetryFramesOut), droppedTelemetryFrames = Interlocked.Read(ref _droppedTelemetryFrames), rateLimitEvents = Interlocked.Read(ref _rateLimitEvents), reconnects = Interlocked.Read(ref _reconnects), authRefreshFailures = Interlocked.Read(ref _authRefreshFailures) }));
    }

    private async Task BroadcastTelemetryAsync(PeerConnection source, CancellationToken cancellationToken)
    {
        var payload = new { type = "TELEMETRY", participantId = source.Identity.ParticipantId, telemetry = source.Telemetry };
        foreach (var peer in _peers.Values.Where(peer => peer.IsConnected && peer.NearbyParticipantIds.Contains(source.Identity.ParticipantId, StringComparer.Ordinal)))
        {
            if (peer.OutboundCount >= 112) peer.TryClose(WebSocketCloseStatus.PolicyViolation, "slow client");
            else peer.TrySendTelemetry(source.Identity.ParticipantId, payload);
            Interlocked.Increment(ref _telemetryFramesOut);
        }
        await Task.CompletedTask;
        _ = cancellationToken;
    }

    private object PeerEvent(string type, PeerConnection peer) => new
    {
        type,
        participantId = peer.Identity.ParticipantId,
        callsign = peer.Identity.Callsign,
        aircraftType = peer.Identity.AircraftType,
        capabilities = peer.Capabilities,
        operationalStates = peer.OperationalStates,
        telemetry = peer.Telemetry
    };

    private static string IdentityVersion(PeerConnection peer) =>
        $"{peer.Identity.Callsign}|{peer.Identity.AircraftType}|{string.Join(',', peer.Capabilities.Order(StringComparer.Ordinal))}|{string.Join(',', peer.OperationalStates.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}";

    private static bool IsValidTelemetry(TacticalTelemetry telemetry) =>
        double.IsFinite(telemetry.LatitudeDeg) && telemetry.LatitudeDeg is >= -90 and <= 90 &&
        double.IsFinite(telemetry.LongitudeDeg) && telemetry.LongitudeDeg is >= -180 and <= 180 &&
        double.IsFinite(telemetry.AltitudeFt) && telemetry.AltitudeFt is >= -2000 and <= 100000 &&
        (!telemetry.HeadingDeg.HasValue || double.IsFinite(telemetry.HeadingDeg.Value) && telemetry.HeadingDeg.Value is >= -360 and <= 720) &&
        (!telemetry.GroundTrackDeg.HasValue || double.IsFinite(telemetry.GroundTrackDeg.Value) && telemetry.GroundTrackDeg.Value is >= -360 and <= 720) &&
        (!telemetry.SpeedKt.HasValue || double.IsFinite(telemetry.SpeedKt.Value) && telemetry.SpeedKt.Value is >= 0 and <= 1500) &&
        (!telemetry.PitchDeg.HasValue || double.IsFinite(telemetry.PitchDeg.Value) && telemetry.PitchDeg.Value is >= -90 and <= 90) &&
        (!telemetry.BankDeg.HasValue || double.IsFinite(telemetry.BankDeg.Value) && telemetry.BankDeg.Value is >= -180 and <= 180) &&
        (!telemetry.VerticalSpeedFpm.HasValue || double.IsFinite(telemetry.VerticalSpeedFpm.Value) && System.Math.Abs(telemetry.VerticalSpeedFpm.Value) <= 20000) &&
        (!telemetry.VelocityNorthMps.HasValue || double.IsFinite(telemetry.VelocityNorthMps.Value)) &&
        (!telemetry.VelocityEastMps.HasValue || double.IsFinite(telemetry.VelocityEastMps.Value)) &&
        (!telemetry.VelocityDownMps.HasValue || double.IsFinite(telemetry.VelocityDownMps.Value));

    private static HashSet<string> CapabilitiesFor(string? aircraftType)
    {
        var capabilities = new HashSet<string>(["identity", "telemetry"], StringComparer.Ordinal);
        var profile = AircraftCapabilities.Resolve(aircraftType);
        if (profile.CanTanker) capabilities.Add("aar.tanker");
        if (profile.CanReceive) capabilities.Add("aar.receiver");
        return capabilities;
    }

    private static double ReadRadius() => double.TryParse(Environment.GetEnvironmentVariable("TACTICAL_LINK_INTEREST_RADIUS_NM"), out var radius) ? System.Math.Clamp(radius, 1, ReadMaxRadius()) : 200;
    private static double ReadMaxRadius() => double.TryParse(Environment.GetEnvironmentVariable("TACTICAL_LINK_MAX_INTEREST_RADIUS_NM"), out var radius) ? System.Math.Clamp(radius, 1, 1000) : 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class PeerConnection : IDisposable
    {
        private CancellationTokenSource? _graceCts;
        private WebSocket _socket;
        private readonly Channel<string> _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        private readonly object _telemetryGate = new();
        private readonly Dictionary<string, string> _latestTelemetry = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _wakeWriter = new(0);
        private volatile bool _disposed;
        public PeerConnection(ParticipantIdentity identity, long generation, WebSocket socket)
        {
            Identity = identity;
            Generation = generation;
            _socket = socket;
            IsConnected = true;
            Capabilities = CapabilitiesFor(identity.AircraftType);
            if (Capabilities.Contains("aar.tanker")) OperationalStates["tankerAvailability"] = "Off";
        }
        public ParticipantIdentity Identity { get; private set; }
        public long Generation { get; }
        public bool IsConnected { get; set; }
        public bool InReconnectGrace { get; set; }
        public bool ExplicitlyDisconnected { get; set; }
        public TacticalTelemetry? Telemetry { get; set; }
        public DateTimeOffset LastTelemetryReceivedAt { get; set; }
        public long LastSequence { get; set; }
        public int RateLimitViolations { get; set; }
        public double InterestRadiusNm { get; set; } = 200;
        public HashSet<string> NearbyParticipantIds { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PeerIdentityVersions { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Capabilities { get; private set; }
        public Dictionary<string, string> OperationalStates { get; private set; } = [];
        public int OutboundCount { get { lock (_telemetryGate) return _outbound.Reader.Count + _latestTelemetry.Count; } }
        public bool IsSlow => OutboundCount > 112;
        private bool _closeAfterDrain;
        private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
        private string _closeReason = "closed";
        public PeerConnection Resume(ParticipantIdentity identity, long generation, WebSocket socket)
        {
            var resumed = new PeerConnection(identity, generation, socket)
            {
                Telemetry = Telemetry,
                LastTelemetryReceivedAt = LastTelemetryReceivedAt,
                LastSequence = 0,
                InterestRadiusNm = InterestRadiusNm,
                NearbyParticipantIds = new HashSet<string>(NearbyParticipantIds, StringComparer.Ordinal),
                PeerIdentityVersions = new Dictionary<string, string>(PeerIdentityVersions, StringComparer.Ordinal)
            };
            if (resumed.Capabilities.Contains("aar.tanker") && OperationalStates.TryGetValue("tankerAvailability", out var availability))
                resumed.OperationalStates["tankerAvailability"] = availability;
            return resumed;
        }

        public void StartWriter(CancellationToken cancellationToken) => _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _wakeWriter.WaitAsync(cancellationToken);
                    if (!_outbound.Reader.TryRead(out var json))
                    {
                        lock (_telemetryGate)
                        {
                            var item = _latestTelemetry.FirstOrDefault();
                            if (item.Key is not null)
                            {
                                json = item.Value;
                                _latestTelemetry.Remove(item.Key);
                            }
                            else json = string.Empty;
                        }
                        if (json.Length == 0)
                        {
                            if (_closeAfterDrain)
                            {
                                await _socket.CloseOutputAsync(_closeStatus, _closeReason, CancellationToken.None);
                                return;
                            }
                            continue;
                        }
                    }
                    var bytes = Encoding.UTF8.GetBytes(json);
                    if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                    if (_closeAfterDrain && _outbound.Reader.Count == 0)
                    {
                        await _socket.CloseOutputAsync(_closeStatus, _closeReason, CancellationToken.None);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }, CancellationToken.None);

        public bool TrySend(object value)
        {
            if (_disposed) return false;
            var json = SerializeVersioned(value);
            if (!_outbound.Writer.TryWrite(json)) return false;
            _wakeWriter.Release();
            return true;
        }

        public void TrySendTelemetry(string participantId, object value)
        {
            if (_disposed) return;
            var json = SerializeVersioned(value);
            lock (_telemetryGate)
            {
                if (_disposed) return;
                var isNew = !_latestTelemetry.ContainsKey(participantId);
                _latestTelemetry[participantId] = json;
                if (isNew) _wakeWriter.Release();
            }
        }

        private static string SerializeVersioned(object value)
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
            payload["protocolVersion"] = JsonSerializer.SerializeToElement(TacticalLinkProtocol.Version);
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        public void RemoveTelemetry(string participantId)
        {
            lock (_telemetryGate) _latestTelemetry.Remove(participantId);
        }

        public void UpdateOperationalStates(JsonElement root)
        {
            if (!Capabilities.Contains("aar.tanker"))
            {
                OperationalStates.Remove("tankerAvailability");
                return;
            }
            if (root.TryGetProperty("operationalStates", out var states) && states.ValueKind == JsonValueKind.Object && Capabilities.Contains("aar.tanker"))
            {
                var requested = states.TryGetProperty("tankerAvailability", out var tanker) && tanker.ValueKind == JsonValueKind.String
                    ? tanker.GetString()
                    : null;
                if (requested is not null && AllowedTankerStates.Contains(requested))
                    OperationalStates["tankerAvailability"] = requested;
            }
        }

        public void MarkDisconnected() { IsConnected = false; InReconnectGrace = true; }
        public void UpdateIdentity(ParticipantIdentity identity)
        {
            var wasTanker = Capabilities.Contains("aar.tanker");
            var availability = OperationalStates.GetValueOrDefault("tankerAvailability", "Off");
            Identity = identity;
            Capabilities = CapabilitiesFor(identity.AircraftType);
            OperationalStates.Clear();
            if (Capabilities.Contains("aar.tanker"))
                OperationalStates["tankerAvailability"] = wasTanker ? availability : "Off";
        }
        public void StartGrace(TimeSpan gracePeriod, TimeProvider timeProvider, Action expire)
        {
            _graceCts?.Cancel(); _graceCts?.Dispose(); _graceCts = new CancellationTokenSource();
            var token = _graceCts.Token;
            _ = Task.Run(async () => { try { await Task.Delay(gracePeriod, timeProvider, token); expire(); } catch (OperationCanceledException) { } });
        }
        public void CancelGrace() { _graceCts?.Cancel(); _graceCts?.Dispose(); _graceCts = null; }
        public void TryClose(WebSocketCloseStatus status, string reason)
        {
            if (_socket.State != WebSocketState.Open) return;
            _closeAfterDrain = true;
            _closeStatus = status;
            _closeReason = reason;
            try { if (_wakeWriter.CurrentCount == 0) _wakeWriter.Release(); } catch (SemaphoreFullException) { }
        }
        public void Dispose()
        {
            _disposed = true;
            CancelGrace();
            _outbound.Writer.TryComplete();
            while (_outbound.Reader.TryRead(out _)) { }
            lock (_telemetryGate) _latestTelemetry.Clear();
            NearbyParticipantIds.Clear();
            PeerIdentityVersions.Clear();
            try { _wakeWriter.Release(); } catch (SemaphoreFullException) { }
        }
    }
}
