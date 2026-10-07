using System.Net.WebSockets;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using TacticalDisplay.App.Cloud;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.TacticalLink;

/// <summary>Explicitly opt-in global TacticalLink connection; owns only transient peer state.</summary>
public sealed class TacticalLinkClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly VtsdCloudClient _cloud;
    private readonly Uri _serverUri;
    private readonly string _clientInstanceId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _telemetryLock = new();
    private readonly object _peerLock = new();
    private readonly Dictionary<string, TacticalPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingModuleMessage> _pendingModuleMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Revision, string Fingerprint)> _operationRevisions = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private Task? _ageRefreshTask;
    private long _sequence;
    private long _moduleTransportSequence;
    private string? _participantId;
    private string? _callsign;
    private DateTimeOffset _authExpiresAt;
    private CancellationToken _applicationToken;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _explicitDisconnectRequested;
    private HashSet<string> _localCapabilities = ["identity", "telemetry"];
    private Dictionary<string, string> _localOperationalStates = [];
    private bool _autoReconnect = true;
    private double _interestRadiusNm = 200;
    private bool _debugDiagnostics;
    private OwnshipState? _previousOwnship;

    public TacticalLinkClient(VtsdCloudClient cloud, Uri? serverUri = null)
    {
        _cloud = cloud;
        _serverUri = serverUri ?? new Uri("wss://link.vtsd.app/v1");
    }

    public TacticalLinkConnectionState ConnectionState { get; private set; } = TacticalLinkConnectionState.Disconnected;
    internal bool ProtocolRejected => _explicitDisconnectRequested && ConnectionState == TacticalLinkConnectionState.Disconnected;
    public IReadOnlyList<TacticalPeer> NearbyPeers { get { lock (_peerLock) return _peers.Values.ToArray(); } }
    public string? LocalParticipantId => _participantId;
    public string? LocalCallsign => _callsign;
    public string? LocalAircraftType { get; private set; }
    public IReadOnlySet<string> LocalCapabilities => _localCapabilities;
    public IReadOnlyDictionary<string, string> LocalOperationalStates => _localOperationalStates;
    public bool LocalTankerAvailable => _localOperationalStates.TryGetValue("tankerAvailability", out var state) && state == "Available";
    public bool LocalTankerJoined => _localOperationalStates.TryGetValue("tankerAvailability", out var state) && state != "Off";
    public event EventHandler? StateChanged;
    public event EventHandler<TacticalLinkModuleEvent>? ModuleEventReceived;
    public event EventHandler<string>? ModuleProtocolError;

    public async Task ConnectAsync(CancellationToken cancellationToken, bool autoReconnect = true, double interestRadiusNm = 200, bool debugDiagnostics = false)
    {
        if (ConnectionState is TacticalLinkConnectionState.Connected or TacticalLinkConnectionState.Connecting) return;
        _explicitDisconnectRequested = false;
        _applicationToken = cancellationToken;
        _autoReconnect = autoReconnect;
        _interestRadiusNm = System.Math.Clamp(interestRadiusNm, 10, 500);
        _debugDiagnostics = debugDiagnostics;
        _reconnectCts?.Cancel();
        SetState(TacticalLinkConnectionState.Connecting);
        try { await ConnectCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            MarkConnectionFailed();
            throw;
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var token = await _cloud.CreateTacticalLinkTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token) || string.IsNullOrWhiteSpace(token.Callsign))
            throw new CloudApiException("VTSD Cloud did not return a complete TacticalLink identity.", errorCode: "TACTICAL_LINK_IDENTITY");

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token.Token}");
        _socket = socket;
        Interlocked.Exchange(ref _moduleTransportSequence, 0);
        await socket.ConnectAsync(_serverUri, cancellationToken).ConfigureAwait(false);
        _callsign = token.Callsign;
        _authExpiresAt = token.ExpiresAt;
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetState(TacticalLinkConnectionState.Connected);
        LocalAircraftType = token.AircraftType;
        await SendAsync(new { type = "CONNECT", clientInstanceId = _clientInstanceId }, cancellationToken).ConfigureAwait(false);
        await SendAsync(new { type = "INTEREST_UPDATE", radiusNm = _interestRadiusNm }, cancellationToken).ConfigureAwait(false);
        if (_debugDiagnostics) DataSourceDebugLog.Info("TacticalLink", $"Connected | callsign={token.Callsign} interestRadiusNm={_interestRadiusNm:0}");
        _receiveTask = ReceiveLoopAsync(socket, _connectionCts.Token);
        _ageRefreshTask = RefreshPeerAgeLoopAsync(_connectionCts.Token);
        _ = AuthRefreshLoopAsync(_connectionCts.Token);
    }

    public async Task PublishTelemetryAsync(OwnshipState ownship, CancellationToken cancellationToken)
    {
        if (ConnectionState != TacticalLinkConnectionState.Connected) return;
        var groundTrack = ownship.GroundTrackDeg ?? ownship.HeadingDeg;
        var horizontalSpeedMps = ownship.SpeedKt * 0.514444;
        var trackRadians = groundTrack * (System.Math.PI / 180.0);
        double? verticalSpeedFpm = null;
        lock (_telemetryLock)
        {
            if (_previousOwnship is { } previous)
            {
                var elapsed = (ownship.Timestamp - previous.Timestamp).TotalSeconds;
                if (elapsed is > 0.05 and <= 3)
                {
                    var rate = (ownship.AltitudeFt - previous.AltitudeFt) * 60.0 / elapsed;
                    if (double.IsFinite(rate) && System.Math.Abs(rate) <= 20000) verticalSpeedFpm = rate;
                }
            }
            _previousOwnship = ownship;
        }
        var telemetry = new
        {
            type = "TELEMETRY",
            sequence = Interlocked.Increment(ref _sequence),
            sampleTimestampUtc = ownship.Timestamp,
            latitudeDeg = ownship.LatitudeDeg,
            longitudeDeg = ownship.LongitudeDeg,
            altitudeFt = ownship.AltitudeFt,
            headingDeg = ownship.HeadingDeg,
            groundTrackDeg = ownship.GroundTrackDeg,
            speedKt = ownship.SpeedKt,
            pitchDeg = (double?)null,
            bankDeg = (double?)null,
            verticalSpeedFpm,
            velocityNorthMps = horizontalSpeedMps is { } northSpeed ? northSpeed * System.Math.Cos(trackRadians) : (double?)null,
            velocityEastMps = horizontalSpeedMps is { } eastSpeed ? eastSpeed * System.Math.Sin(trackRadians) : (double?)null,
            velocityDownMps = verticalSpeedFpm.HasValue ? -verticalSpeedFpm.Value * 0.00508 : (double?)null
        };
        if (!await _sendLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var socket = _socket;
            if (socket is { State: WebSocketState.Open })
                await socket.SendAsync(SerializeVersioned(telemetry), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public Task PublishAarPoseAsync(OwnshipState ownship, CancellationToken cancellationToken)
    {
        if (ConnectionState != TacticalLinkConnectionState.Connected) return Task.CompletedTask;
        var groundTrack = ownship.GroundTrackDeg ?? ownship.HeadingDeg;
        var speedMps = ownship.SpeedKt * 0.514444;
        var trackRadians = groundTrack * (System.Math.PI / 180.0);
        double? verticalSpeedMps = null;
        lock (_telemetryLock)
        {
            if (_previousOwnship is { } previous)
            {
                var elapsed = (ownship.Timestamp - previous.Timestamp).TotalSeconds;
                if (elapsed is > 0.05 and <= 3)
                {
                    var value = (ownship.AltitudeFt - previous.AltitudeFt) * 0.3048 / elapsed;
                    if (double.IsFinite(value) && System.Math.Abs(value) <= 100) verticalSpeedMps = -value;
                }
            }
        }
        var messageId = Guid.NewGuid().ToString("N");
        return SendAsync(new
        {
            type = "MODULE_MESSAGE",
            module = "aar",
            moduleProtocolVersion = 1,
            messageId,
            kind = "POSE_UPDATE",
            operationId = (string?)null,
            payload = new
            {
                timestampUtc = ownship.Timestamp,
                latitudeDeg = ownship.LatitudeDeg,
                longitudeDeg = ownship.LongitudeDeg,
                altitudeMeters = ownship.AltitudeFt * 0.3048,
                headingDeg = ownship.HeadingDeg,
                velocityNorthMps = speedMps * System.Math.Cos(trackRadians),
                velocityEastMps = speedMps * System.Math.Sin(trackRadians),
                velocityDownMps = verticalSpeedMps ?? 0
            }
        }, cancellationToken);
    }

    public async Task SetTankerAvailabilityAsync(bool available, CancellationToken cancellationToken)
    {
        if (ConnectionState != TacticalLinkConnectionState.Connected) return;
        await SendModuleMessageAsync("aar", 1, "SET_TANKER_AVAILABILITY", null,
            new { availability = available ? "Available" : "Unavailable" }, cancellationToken).ConfigureAwait(false);
    }

    public Task JoinTankerModeAsync(CancellationToken cancellationToken) =>
        SendModuleMessageAsync("aar", 1, "JOIN_AS_TANKER", null, new { }, cancellationToken);

    public Task LeaveTankerModeAsync(CancellationToken cancellationToken) =>
        SendModuleMessageAsync("aar", 1, "LEAVE_TANKER_MODE", null, new { }, cancellationToken);

    public Task SetProtectedReserveAsync(double reserveKg, CancellationToken cancellationToken) =>
        SendModuleMessageAsync("aar", 1, "SET_PROTECTED_RESERVE", null, new { protectedReserveKg = reserveKg }, cancellationToken);

    public async Task<string> SendModuleMessageAsync(string module, int moduleProtocolVersion, string kind, string? operationId,
        object payload, CancellationToken cancellationToken, string? messageId = null)
    {
        if (ConnectionState != TacticalLinkConnectionState.Connected) throw new InvalidOperationException("TacticalLink must be connected to send a module command.");
        var id = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId;
        if (id.Length > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("The module message ID contains unsupported characters.", nameof(messageId));
        var payloadElement = JsonSerializer.SerializeToElement(payload, Json);
        var pendingKey = module + "\n" + id;
        lock (_pendingModuleMessages)
        {
            PrunePendingModuleMessages();
            if (!_pendingModuleMessages.ContainsKey(pendingKey) && _pendingModuleMessages.Count >= 256)
                throw new InvalidOperationException("The pending module command limit has been reached.");
            _pendingModuleMessages[pendingKey] = new PendingModuleMessage(id, module, moduleProtocolVersion, kind, operationId, payloadElement, DateTimeOffset.UtcNow);
        }
        await SendPendingModuleMessageAsync(pendingKey, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public void SetAutoReconnect(bool enabled) => _autoReconnect = enabled;

    public async Task SetInterestRadiusAsync(double radiusNm, CancellationToken cancellationToken)
    {
        _interestRadiusNm = System.Math.Clamp(radiusNm, 10, 500);
        if (ConnectionState == TacticalLinkConnectionState.Connected)
            await SendAsync(new { type = "INTEREST_UPDATE", radiusNm = _interestRadiusNm }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        _explicitDisconnectRequested = true;
        _reconnectCts?.Cancel();
        var socket = _socket;
        _connectionCts?.Cancel();
        SetState(TacticalLinkConnectionState.Disconnected);
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await SendAsync(new { type = "DISCONNECT" }, CancellationToken.None).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        if (_receiveTask is not null) { try { await _receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_reconnectTask is not null) { try { await _reconnectTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        socket?.Dispose();
        _socket = null;
        _connectionCts?.Dispose();
        _connectionCts = null;
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        _reconnectTask = null;
        _receiveTask = null;
        lock (_peerLock) _peers.Clear();
        _participantId = null;
        _callsign = null;
        LocalAircraftType = null;
        _localCapabilities = ["identity", "telemetry"];
        _localOperationalStates = [];
        SetState(TacticalLinkConnectionState.Disconnected);
    }

    public TacticalLinkState Snapshot() => new(ConnectionState, LocalParticipantId, LocalCallsign, NearbyPeers);

    public void MarkConnectionFailed()
    {
        _socket?.Dispose();
        _socket = null;
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _receiveTask = null;
        LocalAircraftType = null;
        _localCapabilities = ["identity", "telemetry"];
        _localOperationalStates = [];
        _participantId = null;
        _callsign = null;
        lock (_peerLock) _peers.Clear();
        SetState(TacticalLinkConnectionState.Disconnected);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 256 * 1024) throw new WebSocketException("TacticalLink message exceeded the size limit.");
                } while (!result.EndOfMessage);
                HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or JsonException or IOException)
        {
            DataSourceDebugLog.Warn("TacticalLink", $"Connection interrupted | {ex.Message}");
            SetState(TacticalLinkConnectionState.Degraded);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && !_explicitDisconnectRequested)
            {
                _connectionCts?.Cancel();
                if (_autoReconnect)
                {
                    SetState(TacticalLinkConnectionState.Degraded);
                    if (_reconnectTask is null || _reconnectTask.IsCompleted)
                    {
                        _reconnectCts?.Dispose();
                        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
                        _reconnectTask = ReconnectLoopAsync(_reconnectCts.Token);
                    }
                }
                else SetState(TacticalLinkConnectionState.Disconnected);
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delays = new[] { 1, 2, 5, 10, 20, 30 };
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested && !_explicitDisconnectRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(delays[Math.Min(attempt++, delays.Length - 1)]), cancellationToken).ConfigureAwait(false);
            try
            {
                _socket?.Dispose();
                _socket = null;
                _connectionCts?.Cancel();
                _connectionCts?.Dispose();
                _connectionCts = null;
                SetState(TacticalLinkConnectionState.Connecting);
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                DataSourceDebugLog.Warn("TacticalLink", $"Reconnect attempt failed | {ex.Message}");
                _socket?.Dispose();
                _socket = null;
                SetState(TacticalLinkConnectionState.Degraded);
            }
        }
    }

    private async Task AuthRefreshLoopAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var token = await _cloud.CreateTacticalLinkTokenAsync(cancellationToken).ConfigureAwait(false);
                await SendAsync(new { type = "AUTH_REFRESH", token = token.Token }, cancellationToken).ConfigureAwait(false);
                _authExpiresAt = token.ExpiresAt;
                retryDelay = TimeSpan.FromSeconds(5);
                var remaining = token.ExpiresAt - DateTimeOffset.UtcNow;
                var refreshIn = remaining - TimeSpan.FromSeconds(Random.Shared.Next(60, 91));
                if (refreshIn < TimeSpan.FromSeconds(5)) refreshIn = TimeSpan.FromSeconds(5);
                await Task.Delay(refreshIn, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (CloudApiException ex) when (ex.ErrorCode is "NO_ACTIVE_VATSIM_PILOT" or "UNAUTHORIZED" or "VATSIM_IDENTITY_REQUIRED" or "APP_SESSION_REQUIRED")
            {
                DataSourceDebugLog.Warn("TacticalLink", $"Authentication participation ended | {ex.ErrorCode}");
                _explicitDisconnectRequested = true;
                _connectionCts?.Cancel();
                SetState(TacticalLinkConnectionState.Disconnected);
                return;
            }
            catch (Exception ex) when (ex is CloudApiException or WebSocketException or ObjectDisposedException or HttpRequestException)
            {
                DataSourceDebugLog.Warn("TacticalLink", $"Authentication refresh failed; retrying | {ex.Message}");
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(System.Math.Min(60, retryDelay.TotalSeconds * 2));
            }
        }
    }

    private async Task RefreshPeerAgeLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal void HandleMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != TacticalLinkProtocol.Version)
        {
            _explicitDisconnectRequested = true;
            _connectionCts?.Cancel();
            SetState(TacticalLinkConnectionState.Disconnected);
            return;
        }
        if (!root.TryGetProperty("type", out var typeNode)) return;
        var type = typeNode.GetString();
        var peerId = root.TryGetProperty("participantId", out var idNode) ? idNode.GetString() : null;
        if (type == "PEER_LEAVE" && peerId is not null)
        {
            lock (_peerLock) _peers.Remove(peerId);
        }
        else if (type is "PEER_ENTER" or "PEER_UPDATE" && peerId is not null)
        {
            var peer = JsonSerializer.Deserialize<TacticalPeerWire>(root.GetRawText(), Json);
            if (peer is not null) lock (_peerLock) _peers[peerId] = peer.ToDomain();
        }
        else if (type == "TELEMETRY" && peerId is not null)
        {
            var telemetry = JsonSerializer.Deserialize<TacticalTelemetry>(root.GetProperty("telemetry").GetRawText(), Json);
            if (telemetry is not null)
            {
                lock (_peerLock)
                    if (_peers.TryGetValue(peerId, out var existing))
                        _peers[peerId] = existing with { LatestTelemetry = telemetry, TelemetryAge = DateTimeOffset.UtcNow - telemetry.SampleTimestampUtc };
            }
        }
        else if (type == "CONNECTED")
        {
            _participantId = peerId ?? _participantId;
            if (root.TryGetProperty("callsign", out var callsign)) _callsign = callsign.GetString() ?? _callsign;
            if (root.TryGetProperty("aircraftType", out var aircraftType)) LocalAircraftType = aircraftType.GetString();
            UpdateLocalCapabilities(root);
            _ = ResendPendingModuleMessagesAsync(_connectionCts?.Token ?? CancellationToken.None);
            if (_localCapabilities.Contains("aar.tanker") || _localCapabilities.Contains("aar.receiver")) _ = RequestAarStateAsync(_connectionCts?.Token ?? CancellationToken.None);
        }
        else if (type == "AUTH_REFRESHED")
        {
            if (root.TryGetProperty("callsign", out var refreshedCallsign)) _callsign = refreshedCallsign.GetString() ?? _callsign;
            if (root.TryGetProperty("aircraftType", out var aircraftType)) LocalAircraftType = aircraftType.GetString();
            UpdateLocalCapabilities(root);
            if (root.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.TryGetDateTimeOffset(out var refreshedExpiry))
                _authExpiresAt = refreshedExpiry;
        }
        else if (type == "CAPABILITY_UPDATED")
        {
            UpdateLocalCapabilities(root);
        }
        else if (type == "MODULE_EVENT")
        {
            HandleModuleEvent(root);
        }
        else if (type == "ERROR" && root.TryGetProperty("code", out var codeNode) && codeNode.GetString() is "AUTH_REFRESH_REJECTED" or "IDENTITY_MISMATCH")
        {
            _explicitDisconnectRequested = true;
            _connectionCts?.Cancel();
            SetState(TacticalLinkConnectionState.Disconnected);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;
        var bytes = SerializeVersioned(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private void HandleModuleEvent(JsonElement root)
    {
        if (!root.TryGetProperty("module", out var moduleNode) || moduleNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("moduleProtocolVersion", out var versionNode) || !versionNode.TryGetInt32(out var version) ||
            !root.TryGetProperty("transportSequence", out var sequenceNode) || !sequenceNode.TryGetInt64(out var transportSequence) ||
            !root.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("payload", out var payload))
        {
            ModuleProtocolError?.Invoke(this, "Malformed module event envelope.");
            return;
        }
        var lastTransport = Interlocked.Read(ref _moduleTransportSequence);
        if (transportSequence <= lastTransport) return;
        Interlocked.Exchange(ref _moduleTransportSequence, transportSequence);
        var module = moduleNode.GetString()!;
        var kind = kindNode.GetString()!;
        var operationId = root.TryGetProperty("operationId", out var opNode) && opNode.ValueKind == JsonValueKind.String ? opNode.GetString() : null;
        if (root.TryGetProperty("messageId", out var messageNode) && messageNode.ValueKind == JsonValueKind.String && messageNode.GetString() is { } messageId)
        {
            lock (_pendingModuleMessages) _pendingModuleMessages.Remove(module + "\n" + messageId);
        }
        long? revision = root.TryGetProperty("operationRevision", out var revisionNode) && revisionNode.ValueKind == JsonValueKind.Number && revisionNode.TryGetInt64(out var parsedRevision)
            ? parsedRevision : null;
        if (revision is < 1 || (revision.HasValue && string.IsNullOrWhiteSpace(operationId)))
        {
            ModuleProtocolError?.Invoke(this, "Operation events require an operation ID and positive revision.");
            return;
        }
        if (revision is { } currentRevision)
        {
            var key = module + ":" + operationId;
            var fingerprint = kind + "\n" + CanonicalJson(payload);
            if (_operationRevisions.TryGetValue(key, out var previous))
            {
                if (currentRevision < previous.Revision) return;
                if (currentRevision == previous.Revision)
                {
                    if (!StringComparer.Ordinal.Equals(previous.Fingerprint, fingerprint))
                        ModuleProtocolError?.Invoke(this, $"Conflicting AAR event content at operation revision {currentRevision}; request a fresh snapshot.");
                    return;
                }
            }
            _operationRevisions[key] = (currentRevision, fingerprint);
        }
        ModuleEventReceived?.Invoke(this, new TacticalLinkModuleEvent(module, version, transportSequence, kind, operationId, revision, payload.Clone()));
    }

    private async Task ResendPendingModuleMessagesAsync(CancellationToken cancellationToken)
    {
        string[] ids;
        lock (_pendingModuleMessages)
        {
            PrunePendingModuleMessages();
            ids = _pendingModuleMessages.Keys.ToArray();
        }
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await SendPendingModuleMessageAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestAarStateAsync(CancellationToken cancellationToken)
    {
        try { await SendModuleMessageAsync("aar", 1, "GET_STATE", null, new { }, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or WebSocketException or ObjectDisposedException)
        {
            DataSourceDebugLog.Debug("TacticalLink", $"AAR state snapshot request was skipped | {ex.GetType().Name}");
        }
    }

    private async Task SendPendingModuleMessageAsync(string id, CancellationToken cancellationToken)
    {
        PendingModuleMessage? message;
        lock (_pendingModuleMessages) _pendingModuleMessages.TryGetValue(id, out message);
        if (message is null || ConnectionState != TacticalLinkConnectionState.Connected) return;
        await SendAsync(new
        {
            type = "MODULE_MESSAGE",
            module = message.Module,
            moduleProtocolVersion = message.ProtocolVersion,
            messageId = message.MessageId,
            kind = message.Kind,
            operationId = message.OperationId,
            payload = message.Payload
        }, cancellationToken).ConfigureAwait(false);
    }

    private void PrunePendingModuleMessages()
    {
        var threshold = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        foreach (var id in _pendingModuleMessages.Where(pair => pair.Value.CreatedAt < threshold).Select(pair => pair.Key).ToArray())
            _pendingModuleMessages.Remove(id);
    }

    private static string CanonicalJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}";
        if (element.ValueKind == JsonValueKind.Array) return "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]";
        return element.GetRawText();
    }

    internal static byte[] SerializeVersioned(object message)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(message, Json), Json)!;
        payload["protocolVersion"] = JsonSerializer.SerializeToElement(TacticalLinkProtocol.Version);
        return JsonSerializer.SerializeToUtf8Bytes(payload, Json);
    }

    private void SetState(TacticalLinkConnectionState state)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        if (state == TacticalLinkConnectionState.Disconnected)
        {
            lock (_peerLock) _peers.Clear();
            _participantId = null;
            _callsign = null;
            LocalAircraftType = null;
            _localCapabilities = ["identity", "telemetry"];
            _localOperationalStates = [];
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLocalCapabilities(JsonElement root)
    {
        if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            _localCapabilities = capabilities.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (root.TryGetProperty("operationalStates", out var states) && states.ValueKind == JsonValueKind.Object)
            _localOperationalStates = JsonSerializer.Deserialize<Dictionary<string, string>>(states.GetRawText(), Json) ?? [];
        else _localOperationalStates = [];
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private sealed record PendingModuleMessage(string MessageId, string Module, int ProtocolVersion, string Kind, string? OperationId, JsonElement Payload, DateTimeOffset CreatedAt);

    private sealed class TacticalPeerWire
    {
        public string ParticipantId { get; set; } = "";
        public string Callsign { get; set; } = "";
        public string? AircraftType { get; set; }
        public HashSet<string> Capabilities { get; set; } = [];
        public Dictionary<string, string> OperationalStates { get; set; } = [];
        public TacticalTelemetry? Telemetry { get; set; }
        public TacticalPeer ToDomain() => new(ParticipantId, Callsign, AircraftType, Capabilities, OperationalStates, Telemetry,
            Telemetry is null ? null : DateTimeOffset.UtcNow - Telemetry.SampleTimestampUtc);
    }
}
