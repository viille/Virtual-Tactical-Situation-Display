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

public sealed record AuthenticatedParticipant(string ParticipantId, string UserId, string VatsimCid, string Callsign, DateTimeOffset ExpiresAt);

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
            var participantId = Claim("participant_id");
            var cid = Claim("vatsim_cid");
            var callsign = Claim("callsign");
            if (string.IsNullOrWhiteSpace(userId) || Claim("sub") != userId || participantId != userId ||
                string.IsNullOrWhiteSpace(cid) || !Guid.TryParse(participantId, out _) ||
                string.IsNullOrWhiteSpace(callsign) || callsign.Length > 16)
                throw new UnauthorizedAccessException();
            return new(participantId, userId, cid, callsign, DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()));
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
    private static readonly HashSet<string> AllowedCapabilities = ["identity", "telemetry", "aar.receiver", "aar.tanker", "aar.boom_operator", "vtsc", "formation"];
    private static readonly HashSet<string> AllowedTankerStates = ["Off", "Available", "Busy", "Unavailable"];
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly IPeerInterestResolver _interestResolver;
    private readonly TimeSpan _reconnectGrace = TimeSpan.FromSeconds(12);
    private readonly double _defaultRadiusNm = ReadRadius();
    private readonly double _maxRadiusNm = ReadMaxRadius();
    private readonly TimeSpan _telemetryStaleAfter = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _interestLock = new(1, 1);
    private int _interestDirty;

    public TacticalLinkHub(IPeerInterestResolver interestResolver)
    {
        _interestResolver = interestResolver;
        _ = Task.Run(InterestRefreshLoopAsync);
        _ = Task.Run(TelemetryExpiryLoopAsync);
    }

    public async Task RunPeerAsync(WebSocket socket, AuthenticatedParticipant identity, CancellationToken cancellationToken)
    {
        var generation = _generations.AddOrUpdate(identity.ParticipantId, 1, static (_, current) => current + 1);
        var peer = _peers.AddOrUpdate(identity.ParticipantId,
            _ => new PeerConnection(identity, generation, socket),
            (_, old) =>
            {
                old.CancelGrace();
                old.TryClose(WebSocketCloseStatus.EndpointUnavailable, "reconnected");
                var resumed = old.Resume(identity, generation, socket);
                old.Dispose();
                return resumed;
            });
        peer.InterestRadiusNm = _defaultRadiusNm;
        peer.StartWriter(cancellationToken);
        peer.TrySend(new { type = "CONNECTED", participantId = peer.Identity.ParticipantId, callsign = peer.Identity.Callsign, connectionGeneration = generation });
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
                    if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Text protocol required.");
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 128 * 1024) throw new WebSocketException("Message limit exceeded.");
                } while (!result.EndOfMessage);
                await HandleMessageAsync(peer, generation, Encoding.UTF8.GetString(message.ToArray()), cancellationToken);
                if (peer.IsSlow) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            if (_peers.TryGetValue(identity.ParticipantId, out var current) && current.Generation == generation)
            {
                current.MarkDisconnected();
                if (current.ExplicitlyDisconnected) RemoveIfGeneration(identity.ParticipantId, generation);
                else current.StartGrace(_reconnectGrace, () => RemoveIfGeneration(identity.ParticipantId, generation));
            }
        }
    }

    private async Task HandleMessageAsync(PeerConnection peer, long generation, string json, CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(peer, generation)) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();
        switch (type)
        {
            case "CONNECT":
                peer.IsConnected = true;
                peer.SetCapabilities(root);
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
                peer.Telemetry = telemetry;
                peer.LastTelemetryReceivedAt = DateTimeOffset.UtcNow;
                if (!IsCurrentGeneration(peer, generation)) return;
                ScheduleInterestRefresh();
                await BroadcastTelemetryAsync(peer, cancellationToken);
                break;
            case "INTEREST_UPDATE":
                var requested = root.TryGetProperty("radiusNm", out var radius) ? radius.GetDouble() : _defaultRadiusNm;
                peer.InterestRadiusNm = System.Math.Clamp(requested, 1, _maxRadiusNm);
                await RefreshInterestAsync(cancellationToken);
                break;
            case "CAPABILITY_UPDATE":
                peer.SetCapabilities(root);
                await RefreshInterestAsync(cancellationToken);
                break;
            case "AUTH_REFRESH":
                if (root.TryGetProperty("token", out var refreshToken) && refreshToken.GetString() is { Length: > 0 } jwt)
                {
                    try
                    {
                        var refreshed = TacticalJwtValidator.Validate("Bearer " + jwt);
                        if (refreshed.ParticipantId == peer.Identity.ParticipantId && refreshed.UserId == peer.Identity.UserId)
                        {
                            peer.UpdateIdentity(refreshed);
                            peer.TrySend(new { type = "AUTH_REFRESHED", callsign = refreshed.Callsign, expiresAt = refreshed.ExpiresAt });
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

    private async Task BroadcastTelemetryAsync(PeerConnection source, CancellationToken cancellationToken)
    {
        var payload = new { type = "TELEMETRY", participantId = source.Identity.ParticipantId, telemetry = source.Telemetry };
        foreach (var peer in _peers.Values.Where(peer => peer.IsConnected && peer.NearbyParticipantIds.Contains(source.Identity.ParticipantId, StringComparer.Ordinal)))
        {
            if (peer.OutboundCount >= 112) peer.TryClose(WebSocketCloseStatus.PolicyViolation, "slow client");
            else peer.TrySendTelemetry(source.Identity.ParticipantId, payload);
        }
        await Task.CompletedTask;
        _ = cancellationToken;
    }

    private object PeerEvent(string type, PeerConnection peer) => new
    {
        type,
        participantId = peer.Identity.ParticipantId,
        callsign = peer.Identity.Callsign,
        aircraftType = peer.Telemetry?.AircraftType,
        capabilities = peer.Capabilities,
        operationalStates = peer.OperationalStates,
        telemetry = peer.Telemetry
    };

    private static string IdentityVersion(PeerConnection peer) =>
        $"{peer.Identity.Callsign}|{peer.Telemetry?.AircraftType}|{string.Join(',', peer.Capabilities.Order(StringComparer.Ordinal))}|{string.Join(',', peer.OperationalStates.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}";

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
        (!telemetry.VelocityDownMps.HasValue || double.IsFinite(telemetry.VelocityDownMps.Value)) &&
        (telemetry.AircraftType is null || telemetry.AircraftType.Length <= 32 && telemetry.AircraftType.All(character => char.IsLetterOrDigit(character) || character is '-' or ' '));

    private void RemoveIfGeneration(string participantId, long generation)
    {
        if (_peers.TryGetValue(participantId, out var peer) && peer.Generation == generation)
        {
            _peers.TryRemove(participantId, out _);
            peer.Dispose();
            _ = RefreshInterestAsync(CancellationToken.None);
        }
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
        public PeerConnection(AuthenticatedParticipant identity, long generation, WebSocket socket) { Identity = identity; Generation = generation; _socket = socket; IsConnected = true; Capabilities = ["identity", "telemetry"]; }
        public AuthenticatedParticipant Identity { get; private set; }
        public long Generation { get; }
        public bool IsConnected { get; set; }
        public bool InReconnectGrace { get; set; }
        public bool ExplicitlyDisconnected { get; set; }
        public TacticalTelemetry? Telemetry { get; set; }
        public DateTimeOffset LastTelemetryReceivedAt { get; set; }
        public long LastSequence { get; set; }
        public double InterestRadiusNm { get; set; } = 200;
        public HashSet<string> NearbyParticipantIds { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PeerIdentityVersions { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Capabilities { get; private set; }
        public Dictionary<string, string> OperationalStates { get; private set; } = [];
        public int OutboundCount { get { lock (_telemetryGate) return _outbound.Reader.Count + _latestTelemetry.Count; } }
        public bool IsSlow => OutboundCount > 112;
        public PeerConnection Resume(AuthenticatedParticipant identity, long generation, WebSocket socket)
        {
            return new PeerConnection(identity, generation, socket)
            {
                Telemetry = Telemetry,
                LastTelemetryReceivedAt = LastTelemetryReceivedAt,
                LastSequence = LastSequence,
                InterestRadiusNm = InterestRadiusNm,
                NearbyParticipantIds = new HashSet<string>(NearbyParticipantIds, StringComparer.Ordinal),
                PeerIdentityVersions = new Dictionary<string, string>(PeerIdentityVersions, StringComparer.Ordinal),
                Capabilities = new HashSet<string>(Capabilities, StringComparer.Ordinal),
                OperationalStates = new Dictionary<string, string>(OperationalStates, StringComparer.Ordinal)
            };
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
                            if (item.Key is null) continue;
                            json = item.Value;
                            _latestTelemetry.Remove(item.Key);
                        }
                    }
                    var bytes = Encoding.UTF8.GetBytes(json);
                    if (_socket.State == WebSocketState.Open) await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }, CancellationToken.None);

        public bool TrySend(object value)
        {
            if (_disposed) return false;
            var json = JsonSerializer.Serialize(value, JsonOptions);
            if (!_outbound.Writer.TryWrite(json)) return false;
            _wakeWriter.Release();
            return true;
        }

        public void TrySendTelemetry(string participantId, object value)
        {
            if (_disposed) return;
            var json = JsonSerializer.Serialize(value, JsonOptions);
            lock (_telemetryGate)
            {
                if (_disposed) return;
                var isNew = !_latestTelemetry.ContainsKey(participantId);
                _latestTelemetry[participantId] = json;
                if (isNew) _wakeWriter.Release();
            }
        }

        public void RemoveTelemetry(string participantId)
        {
            lock (_telemetryGate) _latestTelemetry.Remove(participantId);
        }

        public void SetCapabilities(JsonElement root)
        {
            if (root.TryGetProperty("capabilities", out var values) && values.ValueKind == JsonValueKind.Array)
                Capabilities = values.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null && AllowedCapabilities.Contains(value)).Cast<string>().ToHashSet(StringComparer.Ordinal);
            if (Capabilities.Contains("aar.tanker") && !OperationalStates.ContainsKey("tankerAvailability"))
                OperationalStates["tankerAvailability"] = "Off";
            if (!Capabilities.Contains("aar.tanker")) OperationalStates.Remove("tankerAvailability");
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
        public void UpdateIdentity(AuthenticatedParticipant identity) => Identity = identity;
        public void StartGrace(TimeSpan gracePeriod, Action expire)
        {
            _graceCts?.Cancel(); _graceCts?.Dispose(); _graceCts = new CancellationTokenSource();
            var token = _graceCts.Token;
            _ = Task.Run(async () => { try { await Task.Delay(gracePeriod, token); expire(); } catch (OperationCanceledException) { } });
        }
        public void CancelGrace() { _graceCts?.Cancel(); _graceCts?.Dispose(); _graceCts = null; }
        public void TryClose(WebSocketCloseStatus status, string reason)
        {
            try { if (_socket.State == WebSocketState.Open) _ = _socket.CloseAsync(status, reason, CancellationToken.None); } catch { }
        }
        public void Dispose()
        {
            _disposed = true;
            CancelGrace();
            _outbound.Writer.TryComplete();
            try { _wakeWriter.Release(); } catch (SemaphoreFullException) { }
        }
    }
}
