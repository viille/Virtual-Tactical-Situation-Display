using TacticalDisplay.App.Data;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.TacticalLink;

/// <summary>Owns the desktop client's AAR command and event protocol boundary.</summary>
internal sealed class AarClient : ITacticalLinkClientModule, IDisposable
{
    public const int CurrentProtocolVersion = 1;
    private readonly TacticalLinkClient _transport;
    private readonly CancellationToken _applicationToken;
    private readonly object _poseLock = new();
    private OwnshipState? _previousOwnship;
    private bool _disposed;

    public AarClient(TacticalLinkClient transport, CancellationToken applicationToken)
    {
        _transport = transport;
        _applicationToken = applicationToken;
        _transport.StateChanged += OnTransportStateChanged;
        _transport.RegisterModule(this);
        if (_transport.ConnectionState == TacticalLinkConnectionState.Connected)
            _ = RefreshStateAsync(_applicationToken);
    }

    public string Module => "aar";
    public int ProtocolVersion => CurrentProtocolVersion;
    public bool IsTankerJoined => _transport.LocalOperationalStates.TryGetValue("tankerAvailability", out var state) && state != "Off";
    public bool IsTankerAvailable => _transport.LocalOperationalStates.TryGetValue("tankerAvailability", out var state) && state == "Available";
    public event EventHandler<TacticalLinkModuleEvent>? EventReceived;

    public Task<string> SetTankerAvailabilityAsync(bool available, CancellationToken token) =>
        SendAsync("SET_TANKER_AVAILABILITY", null, new { availability = available ? "Available" : "Unavailable" }, token);

    public Task<string> JoinTankerAsync(CancellationToken token) => SendAsync("JOIN_AS_TANKER", null, new { }, token);
    public Task<string> LeaveTankerAsync(CancellationToken token) => SendAsync("LEAVE_TANKER_MODE", null, new { }, token);
    public Task<string> SetProtectedReserveAsync(double reserveKg, CancellationToken token) =>
        SendAsync("SET_PROTECTED_RESERVE", null, new { protectedReserveKg = reserveKg }, token);

    public Task<string> RequestRefuelAsync(string tankerParticipantId, double? requestedKg, bool full, string transferMode, CancellationToken token) =>
        SendAsync("REQUEST_REFUEL", null, new { tankerParticipantId, full, requestedKg, transferMode }, token);

    public Task<string> AddReceiverAsync(string receiverParticipantId, string transferMode, CancellationToken token) =>
        SendAsync("ADD_RECEIVER_TO_QUEUE", null, new { receiverParticipantId, transferMode }, token);

    public Task<string> RemoveQueueEntryAsync(string requestId, CancellationToken token) =>
        SendAsync("REMOVE_QUEUE_ENTRY", null, new { requestId }, token);

    public Task<string> MoveQueueEntryAsync(string requestId, int position, CancellationToken token) =>
        SendAsync("MOVE_QUEUE_ENTRY", null, new { requestId, position }, token);

    public Task<string> RespondToRequestAsync(string requestId, bool accept, string transferMode, double? plannedKg, CancellationToken token)
    {
        var payload = new Dictionary<string, object?> { ["requestId"] = requestId, ["transferMode"] = transferMode };
        if (plannedKg.HasValue) payload["plannedKg"] = plannedKg.Value;
        return SendAsync(accept ? "ACCEPT_REQUEST" : "REJECT_REQUEST", null, payload, token);
    }

    public Task<string> CancelRequestAsync(string requestId, CancellationToken token) =>
        SendAsync("CANCEL_REQUEST", null, new { requestId }, token);

    public Task<string> SetPlannedOnloadAsync(string operationId, double plannedKg, CancellationToken token) =>
        SendAsync("SET_PLANNED_ONLOAD", operationId, new { plannedKg }, token);

    public Task<string> SetTransferModeAsync(string operationId, string transferMode, CancellationToken token) =>
        SendAsync("SET_TRANSFER_MODE", operationId, new { transferMode }, token);

    public Task<string> ReturnCommittedToPendingAsync(string operationId, CancellationToken token) =>
        SendAsync("RETURN_TO_PENDING", operationId, new { operationId }, token);

    public Task<string> ClearAsternAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("CLEAR_ASTERN", operationId, token);
    public Task<string> HoldAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("HOLD", operationId, token);
    public Task<string> ClearContactAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("CLEAR_CONTACT", operationId, token);
    public Task<string> StartTransferAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("START_TRANSFER", operationId, token);
    public Task<string> StopTransferAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("STOP_TRANSFER", operationId, token);
    public Task<string> DisconnectAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("DISCONNECT", operationId, token);
    public Task<string> BreakawayAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("BREAKAWAY", operationId, token);
    public Task<string> ReconcileAsync(string operationId, CancellationToken token) => SendOperationCommandAsync("RECONCILE", operationId, token);

    public Task<string> PublishPoseAsync(OwnshipState ownship, CancellationToken token)
    {
        var groundTrack = ownship.GroundTrackDeg ?? ownship.HeadingDeg;
        var speedMps = ownship.SpeedKt * 0.514444;
        var radians = groundTrack * (Math.PI / 180.0);
        double velocityDownMps = 0;
        lock (_poseLock)
        {
            if (_previousOwnship is { } previous)
            {
                var elapsed = (ownship.Timestamp - previous.Timestamp).TotalSeconds;
                if (elapsed is > 0.05 and <= 3)
                {
                    var vertical = (ownship.AltitudeFt - previous.AltitudeFt) * 0.3048 / elapsed;
                    if (double.IsFinite(vertical) && Math.Abs(vertical) <= 100) velocityDownMps = -vertical;
                }
            }
            _previousOwnship = ownship;
        }
        return SendAsync("POSE_UPDATE", null,
            new
            {
                timestampUtc = ownship.Timestamp,
                latitudeDeg = ownship.LatitudeDeg,
                longitudeDeg = ownship.LongitudeDeg,
                altitudeMeters = ownship.AltitudeFt * 0.3048,
                headingDeg = ownship.HeadingDeg,
                velocityNorthMps = speedMps * Math.Cos(radians),
                velocityEastMps = speedMps * Math.Sin(radians),
                velocityDownMps
            }, token);
    }

    public Task<string> PublishFuelStatusAsync(double currentFuelKg, double capacityKg, bool adapterReady, double lastAppliedTransferredKg, CancellationToken token) =>
        SendAsync("FUEL_STATUS", null, new { currentFuelKg, capacityKg, adapterReady, lastAppliedTransferredKg }, token);

    public Task RefreshStateAsync(CancellationToken token) => SendAsync("GET_STATE", null, new { }, token);

    public Task<string> AcknowledgeTransferAsync(string operationId, string proposalId, AarFuelProposalResult application, long operationRevision, CancellationToken token) =>
        SendAsync("TRANSFER_ACK", operationId,
            new
            {
                operationId,
                proposalId,
                appliedCumulativeKg = application.AppliedCumulativeKg,
                operationRevision,
                appliedKg = application.AppliedKg,
                status = application.Status.ToString()
            }, token, "transfer-ack-" + proposalId);

    public void HandleEvent(TacticalLinkModuleEvent message)
    {
        if (message.Module != Module || message.ModuleProtocolVersion != ProtocolVersion) return;
        EventReceived?.Invoke(this, message);
    }

    private Task<string> SendOperationCommandAsync(string kind, string operationId, CancellationToken token) =>
        SendAsync(kind, operationId, new { operationId }, token);

    private Task<string> SendAsync(string kind, string? operationId, object payload, CancellationToken token, string? messageId = null) =>
        _transport.SendModuleMessageAsync(Module, ProtocolVersion, kind, operationId, payload, token, messageId);

    private void OnTransportStateChanged(object? sender, EventArgs args)
    {
        if (_transport.ConnectionState == TacticalLinkConnectionState.Connected)
            _ = RefreshStateSafelyAsync();
    }

    private async Task RefreshStateSafelyAsync()
    {
        try { await RefreshStateAsync(_applicationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_applicationToken.IsCancellationRequested) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.UnregisterModule(this);
        EventReceived = null;
    }
}
