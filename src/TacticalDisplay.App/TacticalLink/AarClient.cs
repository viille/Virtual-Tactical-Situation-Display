using TacticalDisplay.App.Data;
using TacticalDisplay.Core.Models;
using System.Text.Json;

namespace TacticalDisplay.App.TacticalLink;

/// <summary>Owns the desktop client's AAR command and event protocol boundary.</summary>
internal sealed class AarClient : ITacticalLinkClientModule, IDisposable
{
    public const int CurrentProtocolVersion = 1;
    private readonly TacticalLinkClient _transport;
    private readonly CancellationToken _applicationToken;
    private readonly object _poseLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, long> _operationRevisions = new(StringComparer.Ordinal);
    private AarState _state = AarState.Empty;
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
    public AarState State { get { lock (_stateLock) return _state; } }
    public event EventHandler<TacticalLinkModuleEvent>? EventReceived;
    public event Action<AarState>? StateChanged;

    public Task<string> SetTankerAvailabilityAsync(bool available, CancellationToken token) =>
        SendAsync("SET_TANKER_AVAILABILITY", null, new { availability = available ? "Available" : "Unavailable" }, token);

    public Task<string> JoinTankerAsync(CancellationToken token) => SendAsync("JOIN_AS_TANKER", null, new { }, token);
    public Task<string> LeaveTankerAsync(CancellationToken token) => SendAsync("LEAVE_TANKER_MODE", null, new { }, token);
    public Task<string> SetProtectedReserveAsync(double reserveKg, CancellationToken token) =>
        SendAsync("SET_PROTECTED_RESERVE", null, new { protectedReserveKg = reserveKg }, token);

    public Task<string> RequestRefuelAsync(string tankerParticipantId, double? requestedKg, bool full, string transferMode, CancellationToken token) =>
        SendAsync("REQUEST_REFUEL", null, new { tankerParticipantId, full, requestedKg, transferMode }, token);

    public Task<string> RequestFullRefuelAsync(string tankerParticipantId, string transferMode, CancellationToken token) =>
        RequestRefuelAsync(tankerParticipantId, null, full: true, transferMode, token);

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

    public Task<string> AcceptRequestAsync(string requestId, string transferMode, double? plannedKg, CancellationToken token) =>
        RespondToRequestAsync(requestId, accept: true, transferMode, plannedKg, token);

    public Task<string> RejectRequestAsync(string requestId, CancellationToken token) =>
        RespondToRequestAsync(requestId, accept: false, "Fuel", null, token);

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
        AarState? updated;
        lock (_stateLock)
        {
            if (message.OperationId is { } operationId && message.OperationRevision is { } revision)
            {
                var previous = _operationRevisions.GetValueOrDefault(operationId);
                if (revision < previous) return;
                _operationRevisions[operationId] = revision;
            }
            _state = Reduce(_state, message);
            updated = _state;
        }
        StateChanged?.Invoke(updated);
        EventReceived?.Invoke(this, message);
    }

    private AarState Reduce(AarState state, TacticalLinkModuleEvent message)
    {
        var payload = message.Payload;
        var operations = new Dictionary<string, AarOperationState>(state.Operations, StringComparer.Ordinal);
        var pending = state.PendingQueue;
        var committedNext = state.CommittedNext;
        var ownPending = state.OwnPendingRequestId;
        var fuel = state.FuelSummary;
        var transferMode = state.TransferMode;
        var lastError = message.Kind == "MODULE_ERROR" && StringValue(payload, "message", out var errorText) ? errorText : null;
        double? maxAllowedKg = message.Kind == "MODULE_ERROR" && NumberValue(payload, "maxAllowedKg", out var maximum) ? maximum : null;

        if (message.Kind == "AAR_STATE")
        {
            operations.Clear();
            if (TryArray(payload, "operations", out var items))
                foreach (var item in items.EnumerateArray())
                    if (TryReadOperation(item) is { } operation)
                    {
                        operations[operation.OperationId] = operation;
                        _operationRevisions[operation.OperationId] = Math.Max(
                            _operationRevisions.GetValueOrDefault(operation.OperationId), operation.Revision);
                    }
            if (TryObject(payload, "queue", out var queue))
            {
                ReadQueue(queue, out pending, out committedNext);
            }
            else
            {
                pending = [];
                committedNext = null;
            }
            fuel = TryObject(payload, "fuel", out var fuelNode) ? ReadFuelSummary(fuelNode) : null;
            ownPending = StringValue(payload, "ownPendingRequestId", out var pendingId) ? pendingId : null;
        }
        else if (message.Kind is "QUEUE_UPDATED" && TryObject(payload, "queue", out var queueUpdate))
        {
            ReadQueue(queueUpdate, out pending, out committedNext);
        }

        if (message.Kind is "REQUEST_ACCEPTED" or "OPERATION_STATE" or "OPERATION_SNAPSHOT" or "OPERATION_RECONCILED" or
            "OPERATION_SUSPENDED" or "OPERATION_COMPLETE" or "OPERATION_FAILED" or "OPERATION_CANCELLED" or
            "OPERATION_DISCONNECTING" or "CLEARED_ASTERN" or "CLEARED_CONTACT" or "CONTACT_CAPTURED" or
            "REFUELING_STARTED" or "TRANSFER_CONFIRMED" or "TRANSFER_STOPPED" or "PLANNED_AMOUNT_REACHED" or "BREAKAWAY" or "HOLD")
        {
            var operationNode = TryObject(payload, "operation", out var nested) ? nested : payload;
            if (TryReadOperation(operationNode, message) is { } operation) operations[operation.OperationId] = operation;
        }

        if (message.Kind == "REQUEST_QUEUED" && TryReadQueueEntry(payload) is { } queued && queued.ReceiverParticipantId == _transport.LocalParticipantId)
            ownPending = queued.RequestId;
        if (message.Kind is "TANKER_ADDED_RECEIVER" or "REQUEST_PENDING" && TryReadQueueEntry(payload) is { } added && added.ReceiverParticipantId == _transport.LocalParticipantId)
            ownPending = added.RequestId;
        if (message.Kind is "REQUEST_ACCEPTED" or "REQUEST_CANCELLED" or "REQUEST_REJECTED" &&
            payload.TryGetProperty("requestId", out var endedRequest) && endedRequest.ValueKind == JsonValueKind.String && endedRequest.GetString() == ownPending)
            ownPending = null;

        var fuelNodePresent = TryObject(payload, "fuel", out var nestedFuel);
        if (fuelNodePresent) fuel = ReadFuelSummary(nestedFuel);
        else if (message.Kind == "RESERVE_UPDATED") fuel = ReadFuelSummary(payload);

        if (payload.TryGetProperty("transferMode", out var modeNode) && modeNode.ValueKind == JsonValueKind.String)
            transferMode = ParseTransferMode(modeNode.GetString());
        else if (message.Kind == "AAR_STATE" &&
            operations.Values.FirstOrDefault(operation => operation.Slot == "Active") is { } activeOperation)
            transferMode = activeOperation.TransferMode;
        if (message.Kind == "TANKER_MODE_JOINED") state = state with { TankerJoined = true };
        if (message.Kind == "TANKER_MODE_LEFT") state = state with { TankerJoined = false, TankerAvailability = "Off" };
        if (message.Kind == "TANKER_MODE_JOINED" && payload.TryGetProperty("availability", out var joinedAvailability) && joinedAvailability.ValueKind == JsonValueKind.String)
            state = state with { TankerAvailability = joinedAvailability.GetString() ?? "Unavailable" };
        if (message.Kind == "TANKER_AVAILABILITY_UPDATED" && payload.TryGetProperty("availability", out var availability) && availability.ValueKind == JsonValueKind.String)
            state = state with { TankerAvailability = availability.GetString() ?? "Off" };

        return state with
        {
            TankerJoined = message.Kind is "AAR_STATE" ? IsTankerJoined : state.TankerJoined,
            TankerAvailability = message.Kind is "AAR_STATE" ? CurrentAvailability : state.TankerAvailability,
            FuelSummary = fuel,
            PendingQueue = pending,
            CommittedNext = committedNext,
            Operations = operations,
            OwnPendingRequestId = ownPending,
            TransferMode = transferMode,
            LastEventKind = message.Kind,
            LastError = lastError,
            MaxAllowedKg = maxAllowedKg
        };
    }

    private string CurrentAvailability => _transport.LocalOperationalStates.TryGetValue("tankerAvailability", out var value) ? value : "Off";

    private static void ReadQueue(JsonElement queue, out IReadOnlyList<AarQueueEntryState> pending, out AarOperationState? committedNext)
    {
        var result = new List<AarQueueEntryState>();
        if (TryArray(queue, "pending", out var items))
            foreach (var item in items.EnumerateArray())
                if (TryReadQueueEntry(item) is { } request) result.Add(request);
        pending = result.OrderBy(item => item.QueueOrder).ToArray();
        committedNext = TryObject(queue, "committedNext", out var committed) ? TryReadOperation(committed) : null;
    }

    private static AarOperationState? TryReadOperation(JsonElement node, TacticalLinkModuleEvent? message = null)
    {
        if (!StringValue(node, "operationId", out var id)) id = message?.OperationId;
        if (id is null) return null;
        var phaseText = StringValue(node, "state", out var stateText) ? stateText : message?.Kind;
        var revision = NumberValue(node, "operationRevision", out var ownRevision) ? (long)ownRevision : message?.OperationRevision ?? 0;
        return new AarOperationState(id,
            StringValue(node, "requestId", out var requestId) ? requestId : null,
            StringValue(node, "tankerParticipantId", out var tankerId) ? tankerId : null,
            StringValue(node, "receiverParticipantId", out var receiverId) ? receiverId : null,
            StringValue(node, "slot", out var slot) ? slot ?? "Active" : "Active",
            AarState.ParsePhase(phaseText), revision,
            NumberValue(node, "plannedKg", out var planned) ? planned : 0,
            NumberValue(node, "transferredKg", out var transferred) ? transferred : 0,
            NumberValue(node, "remainingKg", out var remaining) ? remaining : 0,
            NumberValue(node, "effectiveFlowKgPerSecond", out var flow) ? flow : null,
            StringValue(node, "transferMode", out var mode) ? ParseTransferMode(mode) : AarTransferMode.Fuel,
            BooleanValue(node, "fuelOnAuthorized"));
    }

    private static AarQueueEntryState? TryReadQueueEntry(JsonElement node)
    {
        if (!StringValue(node, "requestId", out var id) || id is null) return null;
        return new AarQueueEntryState(id,
            StringValue(node, "receiverParticipantId", out var receiver) ? receiver : null,
            StringValue(node, "tankerParticipantId", out var tanker) ? tanker : null,
            StringValue(node, "source", out var source) ? source ?? "ReceiverRequest" : "ReceiverRequest",
            StringValue(node, "requestMode", out var requestMode) ? requestMode ?? "Unknown" : "Unknown",
            NumberValue(node, "requestedKg", out var requested) ? requested : null,
            NumberValue(node, "queueOrder", out var order) ? (int)order : 0,
            StringValue(node, "status", out var status) ? status ?? "Pending" : "Pending",
            StringValue(node, "operationId", out var operationId) ? operationId : null);
    }

    private static AarFuelSummaryState ReadFuelSummary(JsonElement node) => new(
        NumberValue(node, "currentFuelKg", out var current) ? current : null,
        NumberValue(node, "protectedReserveKg", out var reserve) ? reserve : null,
        NumberValue(node, "committedFuelKg", out var committed) ? committed : null,
        NumberValue(node, "availableToPromiseKg", out var available) ? available : null);

    private static AarTransferMode ParseTransferMode(string? value) => value == "DryHookup" ? AarTransferMode.DryHookup : AarTransferMode.Fuel;
    private static bool TryObject(JsonElement node, string name, out JsonElement value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
    private static bool TryArray(JsonElement node, string name, out JsonElement value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    }
    private static bool StringValue(JsonElement node, string name, out string? value)
    {
        value = node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return value is not null;
    }
    private static bool NumberValue(JsonElement node, string name, out double value)
    {
        value = node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number) && double.IsFinite(number) ? number : double.NaN;
        return double.IsFinite(value);
    }
    private static bool BooleanValue(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

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
