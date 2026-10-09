using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TacticalLink.Server.Modules;

namespace TacticalLink.Server.Aar;

public sealed record AarServerEvent(string ParticipantId, string Kind, string? OperationId, long? OperationRevision, object Payload);

public sealed class AarModuleHandler(IAarRegistryProvider registry, TimeProvider? timeProvider = null, AarContactConfiguration? contactConfiguration = null) : IModuleMessageHandler, IModuleOperationalStateChangeSource, IHostedService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CompletedResultTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan TransferAckReplayTtl = TimeSpan.FromMinutes(3);
    private const int MaxResultsPerParticipant = 256;
    private const int MaxResultsGlobal = 32_768;
    private const int MaxTransferAckHistoryPerParticipant = MaxOperationsGlobal * 2;
    private const int MaxTransferAckHistoryGlobal = MaxOperationsGlobal * 4;
    private const int MaxSafetyResultsPerParticipant = 64;
    private const int MaxSafetyResultsGlobal = 2_048;
    private const int MaxPendingPerTanker = 8;
    private const int MaxRequestsGlobal = 10_000;
    private const int MaxOperationsGlobal = 2_048;
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AarContactConfiguration _contactConfiguration = contactConfiguration ?? new AarContactConfiguration();
    private readonly Dictionary<string, ParticipantState> _participants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AarRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AarOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, CachedResult>> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TransferAckHistory>> _transferAckHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, CachedResult>> _safetyResults = new(StringComparer.Ordinal);
    private long _nextQueueOrder;
    private CancellationTokenSource? _lifecycleCts;
    private Task? _lifecycleTask;

    public string Module => "aar";
    public int ProtocolVersion => 1;
    public event Action<AarServerEvent>? EventReady;
    public event Action<ModuleOperationalStateChange>? OperationalStateChangeRequested;
    internal int RequestCountForTests { get { lock (_gate) return _requests.Count; } }
    internal (string State, bool FuelOnAuthorized, double TransferredKg)? OperationForTests(string operationId)
    {
        lock (_gate)
            return _operations.TryGetValue(operationId, out var operation)
                ? (operation.State, operation.FuelOnAuthorized, operation.TransferredKg)
                : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifecycleTask = LifecycleLoopAsync(_lifecycleCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifecycleCts?.Cancel();
        if (_lifecycleTask is not null)
        {
            try { await _lifecycleTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (_lifecycleCts?.IsCancellationRequested == true) { }
        }
        _lifecycleCts?.Dispose();
        _lifecycleCts = null;
    }

    public string? OnParticipantConnected(AarPeerSnapshot peer)
    {
        lock (_gate)
        {
            var state = GetParticipant(peer.ParticipantId);
            if (state.ClientInstanceId is { } previousInstance && previousInstance != peer.ClientInstanceId)
            {
                FailOperations(peer.ParticipantId, "PROCESS_RESTART");
                state.TankerJoined = false;
                state.Availability = peer.Capabilities.Contains("aar.tanker") ? "Unavailable" : "Off";
                foreach (var request in _requests.Values.Where(item => item.Status == "Pending" && (item.TankerId == peer.ParticipantId || item.ReceiverId == peer.ParticipantId)))
                {
                    request.Status = "Cancelled";
                    request.TerminalAt = _clock.GetUtcNow();
                }
            }
            if (state.LastIdentity is { } previous && !SameIdentity(previous, peer)) FailOperations(peer.ParticipantId, "IDENTITY_CHANGED");
            state.ClientInstanceId = peer.ClientInstanceId;
            state.LastIdentity = peer;
            state.IsConnected = true;
            state.DisconnectedAt = null;
            state.ReconnectDeadline = null;
            state.Poses.Clear();
            return state.Availability;
        }
    }

    public string? OnParticipantCapabilitiesChanged(AarPeerSnapshot peer)
    {
        lock (_gate)
        {
            UpdateParticipant(peer);
            var state = GetParticipant(peer.ParticipantId);
            if (!peer.Capabilities.Contains("aar.tanker")) state.Availability = "Off";
            return state.Availability;
        }
    }

    public void OnParticipantDisconnected(string userId, string participantId, bool explicitDisconnect)
    {
        lock (_gate)
        {
            if (!_participants.TryGetValue(participantId, out var participant)) return;
            participant.IsConnected = false;
            if (participant.TankerJoined && participant.Availability is "Busy" or "Available")
                SetAvailabilityState(null, participant, "Unavailable");
            participant.DisconnectedAt = _clock.GetUtcNow();
            participant.ReconnectDeadline = _clock.GetUtcNow() + (explicitDisconnect ? TimeSpan.Zero : TimeSpan.FromSeconds(12));
            foreach (var operation in _operations.Values.Where(operation => !IsTerminal(operation.State) && (operation.TankerId == participantId || operation.ReceiverId == participantId)))
            {
                if (operation.TankerId == participantId) operation.TankerReconnectRequired = true;
                if (operation.ReceiverId == participantId) operation.ReceiverReconnectRequired = true;
                Suspend(operation, "NETWORK_LOSS", explicitDisconnect ? TimeSpan.Zero : TimeSpan.FromSeconds(12));
            }
            _ = userId;
        }
    }

    public Task HandleAsync(ModuleCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = context.CurrentPeer;
        if (peer is null || peer.ParticipantId != context.ParticipantId)
        {
            context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = context.MessageId, code = "PARTICIPANT_UNAVAILABLE", message = "Participant state is unavailable." });
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            ExpireResults();
            UpdateParticipant(peer);
            var key = context.MessageId;
            var requestHash = RequestHash(context);
            if (context.Kind == "TRANSFER_ACK" && !HasProposalBoundAckId(context, key))
            {
                context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = key, code = "INVALID_TRANSFER_ACK_ID", message = "TRANSFER_ACK messageId must be transfer-ack-{proposalId}." });
                return Task.CompletedTask;
            }

            if (TryGetCached(context.ParticipantId, key, out var cached))
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(cached.RequestHash), Convert.FromHexString(requestHash)))
                {
                    context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = key, code = "IDEMPOTENCY_CONFLICT", message = "The message ID was already used for a different command." });
                    return Task.CompletedTask;
                }
                context.Reply(cached.Kind, cached.OperationId, cached.OperationRevision, cached.Payload);
                SendCurrentSnapshot(context, cached.RequestId, cached.OperationId);
                return Task.CompletedTask;
            }

            if (TryGetTransferAckHistory(context.ParticipantId, key, out var ackHistory))
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ackHistory.RequestHash), Convert.FromHexString(requestHash)))
                {
                    context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = key, code = "IDEMPOTENCY_CONFLICT", message = "The message ID was already used for a different command." });
                    return Task.CompletedTask;
                }

                if (ackHistory.Response.Kind == "MODULE_ERROR")
                {
                    context.Reply(ackHistory.Response.Kind, ackHistory.Response.OperationId, ackHistory.Response.OperationRevision, ackHistory.Response.Payload);
                    return Task.CompletedTask;
                }

                var operation = ackHistory.OperationId is { } operationId ? _operations.GetValueOrDefault(operationId) : null;
                if (operation is not null && (operation.TankerId == context.ParticipantId || operation.ReceiverId == context.ParticipantId))
                    SendCurrentSnapshot(context, null, operation.Id);
                else
                    context.Reply(ackHistory.Response.Kind, ackHistory.Response.OperationId, ackHistory.Response.OperationRevision, ackHistory.Response.Payload);
                return Task.CompletedTask;
            }

            var safetyCommand = IsSafetyCommand(context.Kind);
            if (RequiresIdempotencyCache(context.Kind) && !safetyCommand && context.Kind != "TRANSFER_ACK" &&
                (GetParticipantEntryCount(context.ParticipantId) >= MaxResultsPerParticipant || GetGlobalEntryCount() >= MaxResultsGlobal))
            {
                context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = key, code = "IDEMPOTENCY_CAPACITY", message = "The bounded command result cache is full; retry after expired entries are removed." });
                return Task.CompletedTask;
            }

            var response = Execute(context, peer);
            if (context.Kind != "POSE_UPDATE" || response.Kind == "MODULE_ERROR")
                context.Reply(response.Kind, response.OperationId, response.OperationRevision, response.Payload);
            if (RequiresIdempotencyCache(context.Kind) && context.Kind != "TRANSFER_ACK")
                Remember(context.ParticipantId, key, requestHash, response, context.Kind);
            if (context.Kind == "TRANSFER_ACK" && TryGetString(context.Payload, "proposalId", out var proposalId))
            {
                var ackOperationId = response.OperationId ?? context.OperationId;
                if (ackOperationId is null && TryGetString(context.Payload, "operationId", out var payloadOperationId))
                    ackOperationId = payloadOperationId;
                RememberTransferAck(context.ParticipantId, key, requestHash, proposalId, ackOperationId, response);
            }
            return Task.CompletedTask;
        }
    }

    private CommandResponse Execute(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        try
        {
            return context.Kind switch
            {
                "PING" => Result("PONG", null, null, new { messageId = context.MessageId }),
                "JOIN_AS_TANKER" => JoinTanker(context, peer),
                "LEAVE_TANKER_MODE" => LeaveTanker(context, peer),
                "SET_PROTECTED_RESERVE" => SetReserve(context, peer),
                "SET_TANKER_AVAILABILITY" => SetAvailability(context, peer),
                "FUEL_STATUS" => UpdateFuelStatus(context, peer),
                "POSE_UPDATE" => UpdatePose(context, peer),
                "TRANSFER_ACK" => AcknowledgeTransfer(context, peer),
                "RECONCILE" => Reconcile(context, peer),
                "ADD_RECEIVER_TO_QUEUE" => AddReceiverToQueue(context, peer),
                "REMOVE_QUEUE_ENTRY" => RemoveQueueEntry(context, peer),
                "MOVE_QUEUE_ENTRY" => MoveQueueEntry(context, peer),
                "SET_PLANNED_ONLOAD" => SetPlannedOnload(context, peer),
                "SET_TRANSFER_MODE" => SetTransferMode(context, peer),
                "RETURN_TO_PENDING" => ReturnCommittedToPending(context, peer),
                "CLEAR_ASTERN" => ClearAstern(context, peer),
                "HOLD" => Hold(context, peer),
                "START_TRANSFER" => StartTransfer(context, peer),
                "STOP_TRANSFER" => StopTransfer(context, peer),
                "REQUEST_REFUEL" => RequestRefuel(context, peer),
                "ACCEPT_REQUEST" => AcceptRequest(context, peer),
                "REJECT_REQUEST" => RejectRequest(context, peer),
                "CANCEL_REQUEST" => CancelRequest(context, peer),
                "CANCEL_COMMITMENT" => CancelCommitment(context, peer),
                "CLEAR_CONTACT" => ClearContact(context, peer),
                "DISCONNECT" => ChangeActiveState(context, peer, "Complete", "OPERATION_COMPLETE"),
                "BREAKAWAY" => ChangeActiveState(context, peer, "Breakaway", "BREAKAWAY"),
                "GET_STATE" => GetState(context, peer),
                _ => Result("MODULE_ERROR", context.OperationId, null, new { messageId = context.MessageId, code = "UNKNOWN_AAR_COMMAND", message = "Unsupported AAR command." })
            };
        }
        catch (ArgumentException ex)
        {
            return Result("MODULE_ERROR", context.OperationId, null, new { messageId = context.MessageId, code = "INVALID_AAR_COMMAND", message = ex.Message });
        }
        catch (AarLimitExceededException ex)
        {
            return Result("MODULE_ERROR", context.OperationId, null,
                new { messageId = context.MessageId, code = "PLANNED_AMOUNT_EXCEEDS_SAFE_MAXIMUM", message = ex.Message, maxAllowedKg = ex.MaximumKg });
        }
        catch (InvalidOperationException ex)
        {
            return Result("MODULE_ERROR", context.OperationId, null, new { messageId = context.MessageId, code = "AAR_NOT_ALLOWED", message = ex.Message });
        }
    }

    private CommandResponse JoinTanker(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireCapability(peer, "aar.tanker");
        var state = GetParticipant(peer.ParticipantId);
        if (state.Availability == "Busy") throw new InvalidOperationException("A tanker with an active operation cannot leave or rejoin tanker mode.");
        state.TankerJoined = true;
        state.Availability = "Unavailable";
        context.SetOperationalState("tankerAvailability", state.Availability);
        return Result("TANKER_MODE_JOINED", null, null, new { availability = state.Availability, requiresReserve = !state.ReserveSet });
    }

    private CommandResponse LeaveTanker(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireCapability(peer, "aar.tanker");
        var state = GetParticipant(peer.ParticipantId);
        if (HasNonTerminalOperation(peer.ParticipantId)) throw new InvalidOperationException("Tanker mode cannot be left while an accepted operation exists.");
        foreach (var request in _requests.Values.Where(item => item.TankerId == peer.ParticipantId && item.Status == "Pending").ToArray())
        {
            request.Status = "Cancelled";
            request.TerminalAt = _clock.GetUtcNow();
            Notify(context, request.ReceiverId, "REQUEST_CANCELLED", null, null, RequestViewFor(request, request.ReceiverId));
        }
        Notify(context, peer.ParticipantId, "QUEUE_UPDATED", null, null, QueueView(peer.ParticipantId));
        state.TankerJoined = false;
        state.Availability = "Off";
        context.SetOperationalState("tankerAvailability", "Off");
        return Result("TANKER_MODE_LEFT", null, null, new { availability = "Off" });
    }

    private CommandResponse SetReserve(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var reserve = Number(context.Payload, "protectedReserveKg", 0, 1_000_000);
        var state = GetParticipant(peer.ParticipantId);
        if (state.FuelCapacityKg is { } capacity && reserve > capacity) throw new ArgumentException("Protected reserve cannot exceed current tanker capacity.");
        state.ProtectedReserveKg = reserve;
        state.ReserveSet = true;
        if (state.Availability == "Available" && AvailableToPromise(peer.ParticipantId) <= 0) SetAvailabilityState(context, state, "Unavailable");
        return Result("RESERVE_UPDATED", null, null, FuelSummary(peer.ParticipantId));
    }

    private CommandResponse SetAvailability(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var requested = String(context.Payload, "availability");
        if (requested is not ("Available" or "Unavailable")) throw new ArgumentException("Availability must be Available or Unavailable.");
        var state = GetParticipant(peer.ParticipantId);
        if (requested == "Available" && state.AdapterReady && (!state.ReserveSet || state.CurrentFuelKg is null || state.FuelCapacityKg is null))
            throw new InvalidOperationException("A protected reserve and current fuel state are required when live fuel transfer is available.");
        if (requested == "Available" && state.AdapterReady && AvailableToPromise(peer.ParticipantId) <= 0)
            throw new InvalidOperationException("No tanker fuel is available above the protected reserve and accepted commitments.");
        SetAvailabilityState(context, state, requested);
        return Result("TANKER_AVAILABILITY_UPDATED", null, null, new { availability = requested, fuel = FuelSummary(peer.ParticipantId) });
    }

    private CommandResponse UpdateFuelStatus(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var state = GetParticipant(peer.ParticipantId);
        var current = Number(context.Payload, "currentFuelKg", 0, 2_000_000);
        var capacity = Number(context.Payload, "capacityKg", 0.001, 2_000_000);
        if (current > capacity) throw new ArgumentException("Current fuel cannot exceed capacity.");
        if (!context.Payload.TryGetProperty("adapterReady", out var readyNode) || readyNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("adapterReady is required.");
        var ready = readyNode.GetBoolean();
        state.CurrentFuelKg = current;
        state.FuelCapacityKg = capacity;
        state.AdapterReady = ready;
        state.FuelUpdatedAt = _clock.GetUtcNow();
        state.LastAppliedOperationId = OptionalString(context.Payload, "appliedOperationId");
        state.LastAppliedTransferredKg = state.LastAppliedOperationId is not null && context.Payload.TryGetProperty("lastAppliedTransferredKg", out var watermark) && watermark.ValueKind == JsonValueKind.Number && watermark.TryGetDouble(out var applied) && double.IsFinite(applied) && applied >= 0
            ? applied
            : null;
        if (!ready && state.Availability == "Available") SetAvailabilityState(context, state, "Unavailable");
        if (!ready)
            foreach (var operation in _operations.Values.Where(item => item.TransferMode == "Fuel" && !IsTerminal(item.State) && (item.TankerId == peer.ParticipantId || item.ReceiverId == peer.ParticipantId)))
                Suspend(operation, "FUEL_ADAPTER_UNAVAILABLE", TimeSpan.FromSeconds(5));
        return Result("FUEL_STATUS_ACCEPTED", null, null, new { adapterReady = ready });
    }

    private CommandResponse UpdatePose(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var timestamp = context.Payload.TryGetProperty("timestampUtc", out var timeNode) && timeNode.TryGetDateTimeOffset(out var parsedTime)
            ? parsedTime
            : throw new ArgumentException("timestampUtc is required.");
        if (timestamp > _clock.GetUtcNow().AddSeconds(2) || timestamp < _clock.GetUtcNow().AddSeconds(-3))
            throw new ArgumentException("The pose timestamp is outside the accepted freshness window.");
        var pose = new AarPose(timestamp,
            Number(context.Payload, "latitudeDeg", -90, 90),
            Number(context.Payload, "longitudeDeg", -180, 180),
            Number(context.Payload, "altitudeMeters", -1000, 100000),
            Number(context.Payload, "headingDeg", 0, 359.999999),
            Number(context.Payload, "velocityNorthMps", -1000, 1000),
            Number(context.Payload, "velocityEastMps", -1000, 1000),
            Number(context.Payload, "velocityDownMps", -1000, 1000));
        var state = GetParticipant(peer.ParticipantId);
        if (state.Poses.LastOrDefault() is { } previous && pose.TimestampUtc <= previous.TimestampUtc)
            return Result("POSE_IGNORED", null, null, new { reason = "STALE_OR_DUPLICATE_POSE" });
        state.Poses.Enqueue(pose);
        while (state.Poses.Count > 40) state.Poses.Dequeue();
        EvaluateContact(context, peer.ParticipantId, pose.TimestampUtc);
        return Result("POSE_ACCEPTED", null, null, new { timestampUtc = pose.TimestampUtc });
    }

    private void EvaluateContact(ModuleCommandContext context, string participantId, DateTimeOffset sampleTime)
    {
        foreach (var operation in _operations.Values.Where(item => item.Slot == "Active" && !IsTerminal(item.State) &&
                     (item.TankerId == participantId || item.ReceiverId == participantId)).ToArray())
        {
            var tankerHistory = GetParticipant(operation.TankerId).Poses;
            var receiverHistory = GetParticipant(operation.ReceiverId).Poses;
            if (tankerHistory.Count == 0 || receiverHistory.Count == 0) continue;
            var commonTime = tankerHistory.Last().TimestampUtc < receiverHistory.Last().TimestampUtc
                ? tankerHistory.Last().TimestampUtc
                : receiverHistory.Last().TimestampUtc;
            var tankerPose = At(tankerHistory, commonTime);
            var receiverPose = At(receiverHistory, commonTime);
            if (tankerPose is null || receiverPose is null ||
                !AarContactGeometry.TryMeasure(tankerPose, receiverPose, _contactConfiguration, _clock.GetUtcNow(), out var relative))
            {
                if (operation.State is "Contact" or "Refueling") ReturnToAstern(operation);
                continue;
            }

            var capture = AarContactGeometry.IsInsideCapture(relative, _contactConfiguration);
            var release = AarContactGeometry.IsInsideRelease(relative, _contactConfiguration);
            var now = commonTime;
            if (operation.State == "ClearedContact" && operation.ClearanceValid)
            {
                operation.CaptureSince = capture ? operation.CaptureSince ?? now : null;
                if (operation.CaptureSince is { } captured && now - captured >= _contactConfiguration.EffectiveCaptureDebounce)
                {
                    operation.State = "Contact";
                    operation.Revision++;
                    operation.ContactEstablishedAt = now;
                    Publish(operation.TankerId, "CONTACT_CAPTURED", operation.Id, operation.Revision, OperationView(operation));
                    Publish(operation.ReceiverId, "CONTACT_CAPTURED", operation.Id, operation.Revision, ReceiverOperationView(operation));
                }
            }
            else if (operation.State == "Contact")
            {
                if (capture) operation.ReleaseSince = null;
                else operation.ReleaseSince ??= now;
                if (operation.ReleaseSince is { } released && now - released >= _contactConfiguration.EffectiveReleaseDebounce)
                {
                    ReturnToAstern(operation);
                }
            }
            else if (operation.State == "Refueling")
            {
                operation.ReleaseSince = release ? null : operation.ReleaseSince ?? now;
                if (operation.ReleaseSince is { } released && now - released >= _contactConfiguration.EffectiveReleaseDebounce)
                {
                    ReturnToAstern(operation);
                }
            }
        }
        _ = sampleTime;
    }

    private CommandResponse AcknowledgeTransfer(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation))
            throw new InvalidOperationException("The transfer operation is no longer available.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this operation.");
        var proposalId = String(context.Payload, "proposalId");
        var proposalRevision = Number(context.Payload, "operationRevision", 1, long.MaxValue);
        var targetCumulative = Number(context.Payload, "targetCumulativeKg", 0, 2_000_000);
        var applied = Number(context.Payload, "appliedCumulativeKg", 0, 2_000_000);
        var appliedKg = Number(context.Payload, "appliedKg", 0, 2_000_000);

        if (operation.CancelledTransfer is { } cancelled)
        {
            if (proposalId != cancelled.Proposal.Id)
                return StaleTransferAck(peer, operation);
            if (proposalRevision != cancelled.Proposal.Revision || Math.Abs(targetCumulative - cancelled.Proposal.TargetCumulativeKg) > 0.01)
                throw new InvalidOperationException("The acknowledgement does not match the exact cancelled proposal settlement.");
            if (!IsTerminal(operation.State) && cancelled.Deadline <= _clock.GetUtcNow())
            {
                ExpireCancelledTransferSettlement(operation, cancelled);
                throw new InvalidOperationException("The cancelled transfer settlement expired; explicit reconciliation is required.");
            }
            return SettleCancelledTransfer(context, peer, operation, cancelled, applied, appliedKg);
        }

        if (operation.PendingTransfer is null)
            return StaleTransferAck(peer, operation);
        if (operation.State != "Refueling")
            throw new InvalidOperationException("There is no pending transfer proposal to acknowledge.");
        var pending = operation.PendingTransfer;
        if (proposalId != pending.Id || proposalRevision != pending.Revision)
            return StaleTransferAck(peer, operation);
        if (Math.Abs(targetCumulative - pending.TargetCumulativeKg) > 0.01 ||
            applied + 0.05 < operation.TransferredKg || applied > pending.TargetCumulativeKg + 0.05)
        {
            Suspend(operation, "TRANSFER_ACK_MISMATCH", TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Transfer acknowledgement did not match the outstanding proposal.");
        }
        RecordAppliedWatermark(peer.ParticipantId, operation.Id, applied);
        if (applied + 0.05 < pending.TargetCumulativeKg)
        {
            Suspend(operation, "PARTIAL_FUEL_APPLICATION", TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Partial simulator fuel application paused the operation for reconciliation.");
        }
        if (peer.ParticipantId == operation.TankerId) pending.TankerAppliedKg = applied;
        else pending.ReceiverAppliedKg = applied;
        if (pending.TankerAppliedKg is { } tankerApplied && pending.ReceiverAppliedKg is { } receiverApplied)
        {
            if (Math.Abs(tankerApplied - receiverApplied) > 0.05 || tankerApplied + 0.05 < operation.TransferredKg)
            {
                Suspend(operation, "TRANSFER_ACK_DISAGREEMENT", TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Fuel adapters reported different applied quantities.");
            }
            operation.TransferredKg = Math.Min(operation.PlannedKg, Math.Min(tankerApplied, receiverApplied));
            operation.PendingTransfer = null;
            operation.LastTransferProposalAt = _clock.GetUtcNow();
            operation.Revision++;
            Publish(operation.TankerId, "TRANSFER_CONFIRMED", operation.Id, operation.Revision, OperationView(operation));
            Publish(operation.ReceiverId, "TRANSFER_CONFIRMED", operation.Id, operation.Revision, ReceiverOperationView(operation));
            if (operation.TransferredKg >= operation.PlannedKg - 0.01)
            {
                operation.FuelOnAuthorized = false;
                operation.State = "Contact";
                operation.Revision++;
                Publish(operation.TankerId, "PLANNED_AMOUNT_REACHED", operation.Id, operation.Revision, OperationView(operation));
                Publish(operation.ReceiverId, "PLANNED_AMOUNT_REACHED", operation.Id, operation.Revision, ReceiverOperationView(operation));
            }
        }
        return Result("TRANSFER_ACK_ACCEPTED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation));
    }

    private CommandResponse StaleTransferAck(AarPeerSnapshot peer, AarOperation operation)
    {
        var view = peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation);
        return Result("OPERATION_SNAPSHOT", operation.Id, operation.Revision, view);
    }

    private CommandResponse SettleCancelledTransfer(ModuleCommandContext context, AarPeerSnapshot peer, AarOperation operation,
        CancelledTransferSettlement settlement, double appliedCumulativeKg, double appliedKg)
    {
        var proposal = settlement.Proposal;
        var baseCumulativeKg = proposal.TargetCumulativeKg - proposal.DeltaKg;
        if (appliedCumulativeKg + 0.05 < baseCumulativeKg || appliedCumulativeKg > proposal.TargetCumulativeKg + 0.05 ||
            Math.Abs((appliedCumulativeKg - baseCumulativeKg) - appliedKg) > 0.05)
            throw new InvalidOperationException("The cancelled proposal result is outside its exact cumulative range.");

        var tanker = peer.ParticipantId == operation.TankerId;
        var existing = tanker ? settlement.TankerAppliedKg : settlement.ReceiverAppliedKg;
        if (existing is { } prior && Math.Abs(prior - appliedCumulativeKg) > 0.05)
            throw new InvalidOperationException("A cancelled proposal acknowledgement conflicts with its earlier result.");
        if (tanker) settlement.TankerAppliedKg = appliedCumulativeKg;
        else settlement.ReceiverAppliedKg = appliedCumulativeKg;
        RecordAppliedWatermark(peer.ParticipantId, operation.Id, appliedCumulativeKg);

        if (settlement.TankerAppliedKg is not { } tankerApplied || settlement.ReceiverAppliedKg is not { } receiverApplied)
            return Result("TRANSFER_SETTLEMENT_RECORDED", operation.Id, operation.Revision,
                peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation));

        operation.CancelledTransfer = null;
        if (Math.Abs(tankerApplied - receiverApplied) > 0.05)
        {
            if (!IsTerminal(operation.State))
            {
                operation.SuspendedFromState = operation.State;
                operation.State = "Suspended";
                operation.ClearanceValid = false;
                operation.FuelOnAuthorized = false;
                operation.CaptureSince = null;
                operation.ReleaseSince = null;
                operation.ReconnectDeadline = null;
                operation.Revision++;
                Publish(operation.TankerId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
                    new { operation = OperationView(operation), reason = "CANCELLED_TRANSFER_SETTLEMENT_DISAGREEMENT" });
                Publish(operation.ReceiverId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
                    new { operation = ReceiverOperationView(operation), reason = "CANCELLED_TRANSFER_SETTLEMENT_DISAGREEMENT" });
            }
            throw new InvalidOperationException("The simulator results disagree; the operation is fail-closed for reconciliation.");
        }

        operation.TransferredKg = Math.Min(operation.PlannedKg, Math.Max(operation.TransferredKg, Math.Min(tankerApplied, receiverApplied)));
        operation.Revision++;
        Publish(operation.TankerId, "TRANSFER_ACCOUNTING_SETTLED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "TRANSFER_ACCOUNTING_SETTLED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("TRANSFER_SETTLEMENT_ACCEPTED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation));
    }

    private void RecordAppliedWatermark(string participantId, string operationId, double applied)
    {
        var state = GetParticipant(participantId);
        state.LastAppliedOperationId = operationId;
        state.LastAppliedTransferredKg = applied;
    }

    private void ProposeTransfer(AarOperation operation, DateTimeOffset now)
    {
        if (operation.CancelledTransfer is not null) return;
        if (operation.PendingTransfer is not null)
        {
            if (now - operation.PendingTransfer.CreatedAt > TimeSpan.FromSeconds(3))
                Suspend(operation, "TRANSFER_ACK_TIMEOUT", TimeSpan.FromSeconds(5));
            return;
        }
        if (now - operation.LastTransferProposalAt < TimeSpan.FromSeconds(1)) return;
        var tankerPoses = GetParticipant(operation.TankerId).Poses;
        var receiverPoses = GetParticipant(operation.ReceiverId).Poses;
        if (tankerPoses.Count == 0 || receiverPoses.Count == 0)
        {
            Suspend(operation, "CONTACT_POSE_UNAVAILABLE");
            return;
        }
        var poseTime = tankerPoses.Last().TimestampUtc < receiverPoses.Last().TimestampUtc ? tankerPoses.Last().TimestampUtc : receiverPoses.Last().TimestampUtc;
        var tankerPose = At(tankerPoses, poseTime);
        var receiverPose = At(receiverPoses, poseTime);
        if (tankerPose is null || receiverPose is null ||
            !AarContactGeometry.TryMeasure(tankerPose, receiverPose, _contactConfiguration, now, out var relative))
        {
            Suspend(operation, "CONTACT_GEOMETRY_UNCERTAIN");
            return;
        }
        if (!AarContactGeometry.IsInsideRelease(relative, _contactConfiguration))
        {
            ReturnToAstern(operation);
            return;
        }
        var tanker = GetParticipant(operation.TankerId);
        var receiver = GetParticipant(operation.ReceiverId);
        if (tanker.CurrentFuelKg is null || receiver.CurrentFuelKg is null || receiver.FuelCapacityKg is null)
        {
            Suspend(operation, "FUEL_STATE_UNAVAILABLE", TimeSpan.FromSeconds(5));
            return;
        }
        var otherTankerCommitments = _operations.Values.Where(candidate => candidate.TankerId == operation.TankerId && candidate.Id != operation.Id && !IsTerminal(candidate.State))
            .Sum(candidate => Math.Max(0, candidate.PlannedKg - candidate.TransferredKg));
        var tankerHeadroom = tanker.CurrentFuelKg.Value - tanker.ProtectedReserveKg - otherTankerCommitments;
        var receiverHeadroom = receiver.FuelCapacityKg.Value - receiver.CurrentFuelKg.Value;
        var remaining = operation.PlannedKg - operation.TransferredKg;
        if (tankerHeadroom <= 0.01 || receiverHeadroom <= 0.01 || remaining <= 0.01)
        {
            if (remaining <= 0.01)
            {
                StopAtPlannedAmount(operation);
                return;
            }
            Suspend(operation, tankerHeadroom <= 0.01 ? "TANKER_RESERVE_BOUNDARY" : "RECEIVER_CAPACITY_BOUNDARY", TimeSpan.FromSeconds(5));
            return;
        }
        var amount = Math.Min(operation.EffectiveFlowKgPerSecond, Math.Min(remaining, Math.Min(tankerHeadroom, receiverHeadroom)));
        if (amount <= 0) return;
        var proposal = new AarTransferProposal(Guid.NewGuid().ToString("N"), amount, operation.TransferredKg + amount, now, operation.Revision + 1);
        operation.PendingTransfer = proposal;
        operation.Revision++;
        var tankerPayload = new { proposalId = proposal.Id, participantRole = "Tanker", deltaKg = amount, targetCumulativeKg = proposal.TargetCumulativeKg, operationRevision = operation.Revision };
        Publish(operation.TankerId, "TRANSFER_PROPOSAL", operation.Id, operation.Revision, tankerPayload);
        Publish(operation.ReceiverId, "TRANSFER_PROPOSAL", operation.Id, operation.Revision,
            new { operationId = operation.Id, status = "TRANSFER_IN_PROGRESS", proposalId = proposal.Id, participantRole = "Receiver", deltaKg = amount, targetCumulativeKg = proposal.TargetCumulativeKg, operationRevision = operation.Revision });
    }

    private AarPose? At(Queue<AarPose> poses, DateTimeOffset timestamp)
    {
        var ordered = poses.OrderBy(pose => pose.TimestampUtc).ToArray();
        var before = ordered.LastOrDefault(pose => pose.TimestampUtc <= timestamp);
        var after = ordered.FirstOrDefault(pose => pose.TimestampUtc >= timestamp);
        if (before is not null && after is not null)
        {
            if (before.TimestampUtc == after.TimestampUtc) return before;
            var gap = after.TimestampUtc - before.TimestampUtc;
            if (gap <= _contactConfiguration.EffectiveMaximumAlignmentGap)
            {
                return AarContactGeometry.InterpolatePose(before, after, timestamp);
            }
        }

        return null;
    }

    private void StopAtPlannedAmount(AarOperation operation)
    {
        operation.PendingTransfer = null;
        operation.FuelOnAuthorized = false;
        operation.State = "Contact";
        operation.Revision++;
        Publish(operation.TankerId, "PLANNED_AMOUNT_REACHED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "PLANNED_AMOUNT_REACHED", operation.Id, operation.Revision, ReceiverOperationView(operation));
    }

    private CommandResponse RequestRefuel(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireCapability(peer, "aar.receiver");
        var tankerId = String(context.Payload, "tankerParticipantId");
        var tanker = context.GetPeers().FirstOrDefault(candidate => candidate.ParticipantId == tankerId && candidate.IsConnected)
            ?? throw new InvalidOperationException("The selected tanker is no longer connected.");
        RequireCapability(tanker, "aar.tanker");
        var tankerState = GetParticipant(tankerId);
        var receiverState = GetParticipant(peer.ParticipantId);
        if (HasLiveReceiverWork(peer.ParticipantId)) throw new InvalidOperationException("This receiver already has a pending request or live AAR operation.");
        var transferMode = OptionalString(context.Payload, "transferMode") ?? "Fuel";
        if (transferMode is not ("Fuel" or "DryHookup")) throw new ArgumentException("transferMode must be Fuel or DryHookup.");
        if (transferMode == "Fuel")
        {
            EnsureFreshFuelState(receiverState);
            EnsureFreshFuelState(tankerState);
        }
        if (!tankerState.TankerJoined || tankerState.Availability is not ("Available" or "Busy")) throw new InvalidOperationException("The selected tanker is not Available.");
        var requestAmount = ParseRequestAmount(context.Payload);
        EnsureAarCompatible(peer.AircraftType, tanker.AircraftType);
        if (_requests.Count >= MaxRequestsGlobal) throw new InvalidOperationException("The AAR request history is at capacity.");
        var pending = _requests.Values.Count(request => request.TankerId == tankerId && request.Status == "Pending");
        if (pending >= MaxPendingPerTanker) throw new InvalidOperationException("The tanker pending request queue is full.");
        var id = "req_" + Guid.NewGuid().ToString("N");
        var item = new AarRequest(id, peer.ParticipantId, tankerId, requestAmount, _clock.GetUtcNow(), ++_nextQueueOrder) { TransferMode = transferMode };
        _requests.Add(id, item);
        Notify(context, tankerId, "REQUEST_QUEUED", null, null, RequestView(item));
        Notify(context, peer.ParticipantId, "REQUEST_PENDING", null, null, RequestView(item));
        return Result("REQUEST_QUEUED", null, null, RequestView(item), id);
    }

    private CommandResponse AcceptRequest(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var requestId = String(context.Payload, "requestId");
        if (!_requests.TryGetValue(requestId, out var request) || request.TankerId != peer.ParticipantId || request.Status != "Pending")
            throw new InvalidOperationException("The pending request was not found.");
        var tankerState = GetParticipant(peer.ParticipantId);
        var receiver = context.GetPeers().FirstOrDefault(candidate => candidate.ParticipantId == request.ReceiverId && candidate.IsConnected)
            ?? throw new InvalidOperationException("The receiver is no longer connected.");
        var receiverState = GetParticipant(receiver.ParticipantId);
        var transferMode = OptionalString(context.Payload, "transferMode") ?? request.TransferMode;
        if (transferMode is not ("Fuel" or "DryHookup")) throw new ArgumentException("transferMode must be Fuel or DryHookup.");
        if (transferMode == "Fuel")
        {
            EnsureFreshFuelState(tankerState);
            EnsureFreshFuelState(receiverState);
        }
        if (!tankerState.TankerJoined || tankerState.Availability is not ("Available" or "Busy")) throw new InvalidOperationException("The tanker is not available for acceptance.");
        if (transferMode == "Fuel" && (!tankerState.AdapterReady || !receiverState.AdapterReady)) throw new InvalidOperationException("Both fuel adapters must be ready for live fuel transfer; choose DryHookup without a writable bridge.");
        EnsureAarCompatible(receiver.AircraftType, peer.AircraftType);
        var tankerOps = _operations.Values.Where(operation => operation.TankerId == peer.ParticipantId && !IsTerminal(operation.State)).ToArray();
        if (tankerOps.Count(operation => operation.Slot == "Active") >= 1 && tankerOps.Any(operation => operation.Slot == "CommittedNext"))
            throw new InvalidOperationException("The CommittedNext slot is occupied; the request remains Pending.");
        var slot = tankerOps.Any(operation => operation.Slot == "Active") ? "CommittedNext" : "Active";
        var planned = 0d;
        if (transferMode == "Fuel")
        {
            var committed = tankerOps.Where(operation => operation.TransferMode == "Fuel")
                .Sum(operation => Math.Max(0, operation.PlannedKg - operation.TransferredKg));
            var available = Math.Max(0, tankerState.CurrentFuelKg!.Value - tankerState.ProtectedReserveKg - committed);
            var receiverFree = Math.Max(0, receiverState.FuelCapacityKg!.Value - receiverState.CurrentFuelKg!.Value);
            var maxAllowed = Math.Min(request.RequestedKg ?? receiverFree, Math.Min(receiverFree, available));
            planned = TryNumber(context.Payload, "plannedKg", out var selectedPlan) ? selectedPlan : maxAllowed;
            if (planned > maxAllowed + 0.01) throw new AarLimitExceededException(maxAllowed);
            if (planned <= 0) throw new InvalidOperationException("No safe positive fuel commitment is available; request remains Pending.");
        }
        var registrySnapshot = registry.Current ?? throw new InvalidOperationException("AAR registry is unavailable.");
        if (_operations.Count >= MaxOperationsGlobal) throw new InvalidOperationException("The AAR operation history is at capacity.");
        var tankerProfile = ResolveProfile(registrySnapshot, peer.AircraftType)!;
        var receiverProfile = ResolveProfile(registrySnapshot, receiver.AircraftType)!;
        var flow = ResolveV016AarFlow(tankerProfile, receiverProfile);
        var operation = new AarOperation(
            "aar_" + Guid.NewGuid().ToString("N"), request.Id, peer.ParticipantId, receiver.ParticipantId,
            peer.ClientInstanceId, receiver.ClientInstanceId, peer.ConnectionGeneration, receiver.ConnectionGeneration,
            slot, "Accepted", 1, request.RequestedKg, planned, 0, flow, registrySnapshot.Version, tankerProfile, receiverProfile, peer, receiver, _clock.GetUtcNow());
        operation.TransferMode = transferMode;
        _operations.Add(operation.Id, operation);
        request.Status = "Accepted";
        request.OperationId = operation.Id;
        tankerState.Availability = "Busy";
        context.SetOperationalState("tankerAvailability", "Busy");
        var view = OperationView(operation);
        Notify(context, request.ReceiverId, "REQUEST_ACCEPTED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        Notify(context, peer.ParticipantId, "REQUEST_ACCEPTED", operation.Id, operation.Revision, view);
        Notify(context, peer.ParticipantId, "QUEUE_UPDATED", null, null, QueueView(peer.ParticipantId));
        return Result("REQUEST_ACCEPTED", operation.Id, operation.Revision, view, request.Id);
    }

    private CommandResponse RejectRequest(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var requestId = String(context.Payload, "requestId");
        if (!_requests.TryGetValue(requestId, out var request) || request.TankerId != peer.ParticipantId || request.Status != "Pending")
            throw new InvalidOperationException("The pending request was not found.");
        request.Status = "Rejected";
        request.TerminalAt = _clock.GetUtcNow();
        Notify(context, request.ReceiverId, "REQUEST_REJECTED", null, null, RequestView(request));
        Notify(context, request.TankerId, "QUEUE_UPDATED", null, null, QueueView(request.TankerId));
        return Result("REQUEST_REJECTED", null, null, RequestView(request), request.Id);
    }

    private CommandResponse CancelRequest(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var requestId = String(context.Payload, "requestId");
        if (!_requests.TryGetValue(requestId, out var request) || request.ReceiverId != peer.ParticipantId || request.Status != "Pending")
            throw new InvalidOperationException("Only your own Pending request can be cancelled.");
        request.Status = "Cancelled";
        request.TerminalAt = _clock.GetUtcNow();
        Notify(context, request.TankerId, "REQUEST_CANCELLED", null, null, RequestView(request));
        Notify(context, request.TankerId, "QUEUE_UPDATED", null, null, QueueView(request.TankerId));
        return Result("REQUEST_CANCELLED", null, null, RequestView(request), request.Id);
    }

    private CommandResponse AddReceiverToQueue(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var receiverId = String(context.Payload, "receiverParticipantId");
        var receiver = context.GetPeers().FirstOrDefault(candidate => candidate.ParticipantId == receiverId && candidate.IsConnected)
            ?? throw new InvalidOperationException("The receiver is not connected.");
        RequireCapability(receiver, "aar.receiver");
        if (receiverId == peer.ParticipantId) throw new InvalidOperationException("A tanker cannot add itself to its own queue.");
        if (HasLiveReceiverWork(receiverId)) throw new InvalidOperationException("This receiver already has a pending request or live AAR operation.");
        var tankerState = GetParticipant(peer.ParticipantId);
        var receiverState = GetParticipant(receiverId);
        EnsureAarCompatible(receiver.AircraftType, peer.AircraftType);
        var transferMode = OptionalString(context.Payload, "transferMode") ?? "Fuel";
        if (transferMode is not ("Fuel" or "DryHookup")) throw new ArgumentException("transferMode must be Fuel or DryHookup.");
        if (transferMode == "Fuel")
        {
            EnsureFreshFuelState(tankerState);
            EnsureFreshFuelState(receiverState);
            if (receiverState.FuelCapacityKg is null || receiverState.CurrentFuelKg is null || receiverState.FuelCapacityKg <= receiverState.CurrentFuelKg)
                throw new InvalidOperationException("The receiver has no verified fuel capacity available.");
        }
        if (_requests.Values.Count(item => item.TankerId == peer.ParticipantId && item.Status == "Pending") >= MaxPendingPerTanker)
            throw new InvalidOperationException("The tanker pending queue is full.");
        var item = new AarRequest("req_" + Guid.NewGuid().ToString("N"), receiverId, peer.ParticipantId, null, _clock.GetUtcNow(), ++_nextQueueOrder)
        { Source = "TankerAdded", TransferMode = transferMode };
        _requests.Add(item.Id, item);
        Notify(context, receiverId, "TANKER_ADDED_RECEIVER", null, null, RequestView(item));
        Notify(context, peer.ParticipantId, "QUEUE_UPDATED", null, null, QueueView(peer.ParticipantId));
        return Result("RECEIVER_ADDED_TO_QUEUE", null, null, RequestView(item), item.Id);
    }

    private CommandResponse RemoveQueueEntry(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var requestId = String(context.Payload, "requestId");
        if (!_requests.TryGetValue(requestId, out var request) || request.Status != "Pending")
            throw new InvalidOperationException("Only a pending queue entry can be removed.");
        if (peer.ParticipantId != request.TankerId && peer.ParticipantId != request.ReceiverId)
            throw new InvalidOperationException("Only the tanker or the receiver can remove this pending entry.");
        if (peer.ParticipantId == request.TankerId) RequireTanker(peer);
        request.Status = "Cancelled";
        request.TerminalAt = _clock.GetUtcNow();
        Notify(context, request.TankerId, "QUEUE_UPDATED", null, null, QueueView(request.TankerId));
        Notify(context, request.ReceiverId, "REQUEST_CANCELLED", null, null, new { requestId = request.Id, status = request.Status });
        return Result("QUEUE_ENTRY_REMOVED", null, null, new { requestId, status = request.Status }, requestId);
    }

    private CommandResponse MoveQueueEntry(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var requestId = String(context.Payload, "requestId");
        if (!_requests.TryGetValue(requestId, out var target) || target.TankerId != peer.ParticipantId || target.Status != "Pending")
            throw new InvalidOperationException("Only a pending entry in this tanker queue can be reordered.");
        var position = (int)Number(context.Payload, "position", 0, MaxPendingPerTanker - 1);
        var pending = _requests.Values.Where(item => item.TankerId == peer.ParticipantId && item.Status == "Pending")
            .OrderBy(item => item.QueueOrder).ToList();
        pending.Remove(target);
        pending.Insert(Math.Min(position, pending.Count), target);
        for (var index = 0; index < pending.Count; index++) pending[index].QueueOrder = index;
        Notify(context, peer.ParticipantId, "QUEUE_UPDATED", null, null, QueueView(peer.ParticipantId));
        return Result("QUEUE_UPDATED", null, null, QueueView(peer.ParticipantId));
    }

    private CommandResponse SetPlannedOnload(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || IsTerminal(operation.State))
            throw new InvalidOperationException("No live tanker operation matches this plan update.");
        if (operation.State is "Suspended" or "Disconnecting" || operation.PendingTransfer is not null)
            throw new InvalidOperationException("PlannedKg cannot change while the operation is suspended, disconnecting, or awaiting a fuel acknowledgement.");
        if (operation.TransferMode == "DryHookup") throw new InvalidOperationException("DryHookup operations do not have a fuel plan.");
        var planned = Number(context.Payload, "plannedKg", Math.Max(0.001, operation.TransferredKg), 1_000_000);
        var receiver = GetParticipant(operation.ReceiverId);
        var tanker = GetParticipant(operation.TankerId);
        EnsureFreshFuelState(receiver);
        EnsureFreshFuelState(tanker);
        var receiverMaximum = Math.Max(0, (receiver.FuelCapacityKg ?? 0) - (receiver.CurrentFuelKg ?? 0) + operation.TransferredKg);
        var otherCommitments = _operations.Values.Where(item => item.TankerId == operation.TankerId && item.Id != operation.Id && !IsTerminal(item.State) && item.TransferMode == "Fuel")
            .Sum(item => Math.Max(0, item.PlannedKg - item.TransferredKg));
        var tankerMaximum = Math.Max(0, (tanker.CurrentFuelKg ?? 0) - tanker.ProtectedReserveKg - otherCommitments + operation.TransferredKg);
        var maxAllowed = Math.Min(receiverMaximum, tankerMaximum);
        if (planned > maxAllowed + 0.01) throw new AarLimitExceededException(maxAllowed);
        operation.PlannedKg = planned;
        operation.Revision++;
        Notify(context, operation.TankerId, "PLANNED_ONLOAD_UPDATED", operation.Id, operation.Revision, OperationView(operation));
        Notify(context, operation.ReceiverId, "OPERATION_STATE", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("PLANNED_ONLOAD_UPDATED", operation.Id, operation.Revision, OperationView(operation));
    }

    private CommandResponse SetTransferMode(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.State is not ("Accepted" or "Astern"))
            throw new InvalidOperationException("Transfer mode can only be selected before contact.");
        var mode = String(context.Payload, "transferMode");
        if (mode is not ("Fuel" or "DryHookup")) throw new ArgumentException("transferMode must be Fuel or DryHookup.");
        if (operation.PendingTransfer is not null || operation.TransferredKg > 0)
            throw new InvalidOperationException("Transfer mode cannot change after fuel has been proposed or applied.");
        if (mode == "DryHookup")
        {
            operation.PlannedKg = 0;
            operation.FuelOnAuthorized = false;
        }
        else if (operation.TransferMode == "DryHookup")
        {
            var tanker = GetParticipant(operation.TankerId);
            var receiver = GetParticipant(operation.ReceiverId);
            EnsureFreshFuelState(tanker);
            EnsureFreshFuelState(receiver);
            if (!tanker.AdapterReady || !receiver.AdapterReady) throw new InvalidOperationException("Both writable fuel adapters must be ready for live fuel transfer.");
            var otherCommitments = _operations.Values.Where(item => item.TankerId == operation.TankerId && item.Id != operation.Id && !IsTerminal(item.State) && item.TransferMode == "Fuel")
                .Sum(item => Math.Max(0, item.PlannedKg - item.TransferredKg));
            var available = Math.Max(0, tanker.CurrentFuelKg!.Value - tanker.ProtectedReserveKg - otherCommitments);
            var receiverFree = Math.Max(0, receiver.FuelCapacityKg!.Value - receiver.CurrentFuelKg!.Value);
            var desired = operation.RequestedKg ?? receiverFree;
            operation.PlannedKg = Math.Min(desired, Math.Min(available, receiverFree));
            if (operation.PlannedKg <= 0) throw new InvalidOperationException("No safe positive fuel commitment is available.");
        }
        operation.TransferMode = mode;
        operation.Revision++;
        Publish(operation.TankerId, "TRANSFER_MODE_UPDATED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "OPERATION_STATE", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("TRANSFER_MODE_UPDATED", operation.Id, operation.Revision, OperationView(operation));
    }

    private CommandResponse ClearAstern(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.Slot != "Active" || operation.State != "Accepted" || operation.CancelledTransfer is not null)
            throw new InvalidOperationException("Only the tanker may clear an active staged receiver astern.");
        operation.State = "Astern";
        operation.Revision++;
        operation.ClearanceValid = false;
        Publish(operation.TankerId, "CLEARED_ASTERN", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "CLEARED_ASTERN", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("CLEARED_ASTERN", operation.Id, operation.Revision, OperationView(operation));
    }

    private CommandResponse Hold(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.Slot != "Active" || operation.State is not ("Astern" or "ClearedContact" or "Contact" or "Refueling"))
            throw new InvalidOperationException("HOLD is available only for the tanker active receiver.");
        PreserveCancelledTransfer(operation, "Astern");
        operation.ClearanceValid = false;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.FuelOnAuthorized = false;
        operation.State = operation.State is "Contact" or "Refueling" or "ClearedContact" ? "Astern" : operation.State;
        operation.Revision++;
        Publish(operation.TankerId, "HOLD", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "HOLD", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("HOLD", operation.Id, operation.Revision, OperationView(operation));
    }

    private CommandResponse StartTransfer(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.State != "Contact" || !operation.ClearanceValid || operation.CancelledTransfer is not null)
            throw new InvalidOperationException("START_TRANSFER requires a live tanker-cleared Contact state.");
        if (operation.TransferMode == "DryHookup") throw new InvalidOperationException("Fuel transfer cannot start during DryHookup.");
        if (!IsInsideCurrentCapture(operation)) throw new InvalidOperationException("Fresh contact geometry is required before fuel transfer can start.");
        var tanker = GetParticipant(operation.TankerId);
        var receiver = GetParticipant(operation.ReceiverId);
        EnsureFreshFuelState(tanker);
        EnsureFreshFuelState(receiver);
        if (!tanker.AdapterReady || !receiver.AdapterReady) throw new InvalidOperationException("Both current fuel adapters must be ready.");
        operation.FuelOnAuthorized = true;
        operation.State = "Refueling";
        operation.Revision++;
        Publish(operation.TankerId, "REFUELING_STARTED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "REFUELING_STARTED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("REFUELING_STARTED", operation.Id, operation.Revision, OperationView(operation));
    }

    private CommandResponse StopTransfer(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.State != "Refueling")
            throw new InvalidOperationException("There is no active transfer to stop.");
        PreserveCancelledTransfer(operation, "Contact");
        operation.FuelOnAuthorized = false;
        operation.State = "Contact";
        operation.Revision++;
        Publish(operation.TankerId, "TRANSFER_STOPPED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "TRANSFER_STOPPED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("TRANSFER_STOPPED", operation.Id, operation.Revision, OperationView(operation));
    }

    private void ReturnToAstern(AarOperation operation)
    {
        PreserveCancelledTransfer(operation, "Astern");
        operation.State = "Astern";
        operation.ClearanceValid = false;
        operation.FuelOnAuthorized = false;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.Revision++;
        Publish(operation.TankerId, "CONTACT_RELEASED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "CONTACT_RELEASED", operation.Id, operation.Revision, ReceiverOperationView(operation));
    }

    private CommandResponse ClearContact(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId ||
            operation.Slot != "Active" || operation.State != "Astern")
            throw new InvalidOperationException("Only the tanker may clear contact from Astern.");
        operation.State = "ClearedContact";
        operation.ClearanceValid = true;
        operation.CaptureSince = null;
        operation.Revision++;
        Publish(operation.TankerId, "CLEARED_CONTACT", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "CLEARED_CONTACT", operation.Id, operation.Revision, ReceiverOperationView(operation));
        return Result("CLEARED_CONTACT", operation.Id, operation.Revision, OperationView(operation));
    }

    private bool IsInsideCurrentCapture(AarOperation operation)
    {
        var tankerPoses = GetParticipant(operation.TankerId).Poses;
        var receiverPoses = GetParticipant(operation.ReceiverId).Poses;
        if (tankerPoses.Count == 0 || receiverPoses.Count == 0) return false;
        var timestamp = tankerPoses.Last().TimestampUtc < receiverPoses.Last().TimestampUtc
            ? tankerPoses.Last().TimestampUtc
            : receiverPoses.Last().TimestampUtc;
        var tankerPose = At(tankerPoses, timestamp);
        var receiverPose = At(receiverPoses, timestamp);
        return tankerPose is not null && receiverPose is not null &&
            AarContactGeometry.TryMeasure(tankerPose, receiverPose, _contactConfiguration, _clock.GetUtcNow(), out var relative) &&
            AarContactGeometry.IsInsideCapture(relative, _contactConfiguration);
    }

    private CommandResponse CancelCommitment(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var operationId = String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.Slot != "CommittedNext" || operation.State != "Accepted")
            throw new InvalidOperationException("Only an unstarted CommittedNext operation can be cancelled as a commitment.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this commitment.");
        operation.State = "Cancelled";
        operation.Revision++;
        _requests[operation.RequestId].Status = "Cancelled";
        operation.TerminalAt = _clock.GetUtcNow();
        _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        Notify(context, operation.TankerId, "OPERATION_CANCELLED", operation.Id, operation.Revision, OperationView(operation));
        Notify(context, operation.ReceiverId, "OPERATION_CANCELLED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        Notify(context, operation.TankerId, "QUEUE_UPDATED", null, null, QueueView(operation.TankerId));
        return Result("OPERATION_CANCELLED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation), operation.RequestId);
    }

    private CommandResponse ReturnCommittedToPending(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        RequireTanker(peer);
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.TankerId != peer.ParticipantId || operation.Slot != "CommittedNext" || operation.State != "Accepted")
            throw new InvalidOperationException("Only the unstarted CommittedNext receiver can be returned to Pending.");
        var pending = _requests.Values.Where(item => item.TankerId == operation.TankerId && item.Status == "Pending")
            .OrderBy(item => item.QueueOrder).ToList();
        var position = pending.Count;
        if (TryNumber(context.Payload, "position", out var requestedPosition))
        {
            if (requestedPosition != Math.Truncate(requestedPosition) || requestedPosition < 0 || requestedPosition > pending.Count)
                throw new ArgumentException("position must be an integer between zero and the pending queue length.");
            position = (int)requestedPosition;
        }
        operation.State = "Cancelled";
        operation.Slot = "Terminal";
        operation.Revision++;
        operation.TerminalAt = _clock.GetUtcNow();
        var request = _requests[operation.RequestId];
        request.Status = "Pending";
        request.OperationId = null;
        request.TerminalAt = null;
        pending.Insert(position, request);
        for (var index = 0; index < pending.Count; index++) pending[index].QueueOrder = index;
        _nextQueueOrder = Math.Max(_nextQueueOrder, pending.Count);
        Notify(context, operation.TankerId, "OPERATION_CANCELLED", operation.Id, operation.Revision, OperationView(operation));
        Notify(context, operation.ReceiverId, "REQUEST_PENDING", operation.Id, operation.Revision, new { requestId = request.Id, status = "Pending" });
        Notify(context, operation.TankerId, "QUEUE_UPDATED", null, null, QueueView(operation.TankerId));
        return Result("REQUEST_RETURNED_TO_PENDING", operation.Id, operation.Revision, OperationView(operation), operation.RequestId);
    }

    private CommandResponse ChangeActiveState(ModuleCommandContext context, AarPeerSnapshot peer, string target, string eventKind)
    {
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.Slot != "Active" || IsTerminal(operation.State))
            throw new InvalidOperationException("No active operation matches this command.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this operation.");
        if (target == "Complete")
        {
            if (operation.State is not ("Accepted" or "Astern" or "ClearedContact" or "Contact" or "Refueling" or "Suspended"))
                throw new InvalidOperationException("The active operation cannot disconnect from its current state.");
            if (operation.PendingTransfer is not null)
            {
                // Stop flow immediately, then reconcile the adapter watermark before finalizing
                // disconnect because a local write may already have completed.
                operation.State = "Disconnecting";
                Suspend(operation, "DISCONNECT_DURING_UNCONFIRMED_TRANSFER", TimeSpan.FromSeconds(5));
                return Result("OPERATION_SUSPENDED", operation.Id, operation.Revision,
                    peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation), operation.RequestId);
            }
            operation.FuelOnAuthorized = false;
            operation.ClearanceValid = false;
            operation.State = "Disconnecting";
            operation.Revision++;
            Notify(context, operation.TankerId, "OPERATION_STATE", operation.Id, operation.Revision, OperationView(operation));
            Notify(context, operation.ReceiverId, "OPERATION_STATE", operation.Id, operation.Revision, ReceiverOperationView(operation));
            target = "Complete";
        }
        if (target == "Breakaway") PreserveCancelledTransfer(operation, "Breakaway");
        operation.State = target;
        operation.Revision++;
        if (target is "Breakaway" or "Complete") operation.ClearanceValid = false;
        if (target is "Breakaway" or "Complete") operation.FuelOnAuthorized = false;
        if (target is "Breakaway" or "Complete")
        {
            operation.Slot = "Terminal";
            operation.TerminalAt = _clock.GetUtcNow();
            _requests[operation.RequestId].Status = target;
            _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
            PromoteCommittedNext(context, operation.TankerId);
        }
        Notify(context, operation.TankerId, eventKind, operation.Id, operation.Revision, OperationView(operation));
        Notify(context, operation.ReceiverId, eventKind, operation.Id, operation.Revision, ReceiverOperationView(operation));
        if (target == "Complete")
        {
            operation.Revision++;
            Notify(context, operation.ReceiverId, "GO_ECHELON_RIGHT", operation.Id, operation.Revision, new { operationId = operation.Id, status = "GO_ECHELON_RIGHT" });
        }
        return Result(eventKind, operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation), operation.RequestId);
    }

    private CommandResponse Reconcile(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.State != "Suspended")
            throw new InvalidOperationException("Only a Suspended operation can be reconciled.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this operation.");
        var participants = context.GetPeers().Where(candidate => candidate.IsConnected).ToDictionary(candidate => candidate.ParticipantId, StringComparer.Ordinal);
        if (!participants.TryGetValue(operation.TankerId, out var tanker) || !participants.TryGetValue(operation.ReceiverId, out var receiver))
            throw new InvalidOperationException("Both operation participants must reconnect before reconciliation.");
        if (operation.ReconnectDeadline is { } deadline && _clock.GetUtcNow() >= deadline)
            throw new InvalidOperationException("The reconnect grace period expired.");
        var tankerState = GetParticipant(operation.TankerId);
        var receiverState = GetParticipant(operation.ReceiverId);
        if (!SameIdentity(operation.TankerIdentity, tanker) || !SameIdentity(operation.ReceiverIdentity, receiver) ||
            tanker.ClientInstanceId != operation.TankerClientInstanceId || receiver.ClientInstanceId != operation.ReceiverClientInstanceId ||
            (operation.TankerReconnectRequired && tanker.ConnectionGeneration <= operation.TankerGeneration) ||
            (operation.ReceiverReconnectRequired && receiver.ConnectionGeneration <= operation.ReceiverGeneration))
        {
            FailOperation(operation, "IDENTITY_OR_PROCESS_CHANGED", promoteCommittedNext: operation.Slot == "Active" && peer.ParticipantId != operation.TankerId);
            throw new InvalidOperationException("Participant identity or process changed; operation failed closed.");
        }
        if (!IsFreshPose(tanker.Telemetry) || !IsFreshPose(receiver.Telemetry)) throw new InvalidOperationException("Fresh pose is required before reconciliation.");
        if (operation.TransferMode == "DryHookup")
        {
            operation.TransferredKg = 0;
            operation.CancelledTransfer = null;
        }
        else
        {
            EnsureFreshFuelState(tankerState);
            EnsureFreshFuelState(receiverState);
            if (tankerState.LastAppliedOperationId != operation.Id || receiverState.LastAppliedOperationId != operation.Id ||
                tankerState.LastAppliedTransferredKg is not { } tankerApplied || receiverState.LastAppliedTransferredKg is not { } receiverApplied ||
                Math.Abs(tankerApplied - receiverApplied) > 0.05 || tankerApplied + 0.05 < operation.TransferredKg || tankerApplied > operation.PlannedKg + 0.05)
            {
                FailOperation(operation, "FUEL_STATE_UNRECONCILABLE", promoteCommittedNext: peer.ParticipantId != operation.TankerId);
                throw new InvalidOperationException("Fuel application watermarks disagree or are outside the accepted plan.");
            }

            operation.TransferredKg = Math.Max(operation.TransferredKg, Math.Min(tankerApplied, receiverApplied));
            operation.CancelledTransfer = null;
        }
        operation.ClearanceValid = false;
        operation.ReconnectDeadline = null;
        operation.TankerReconnectRequired = false;
        operation.ReceiverReconnectRequired = false;
        if (operation.SuspendedFromState == "Disconnecting")
        {
            operation.State = "Complete";
            operation.Slot = "Terminal";
            operation.TerminalAt = _clock.GetUtcNow();
            _requests[operation.RequestId].Status = "Complete";
            _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        }
        else operation.State = "Accepted";
        operation.Revision++;
        Publish(operation.TankerId, "OPERATION_RECONCILED", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "OPERATION_RECONCILED", operation.Id, operation.Revision, ReceiverOperationView(operation));
        if (operation.State == "Complete") PromoteCommittedNext(context, operation.TankerId);
        return Result("OPERATION_RECONCILED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation), operation.RequestId);
    }

    private CommandResponse GetState(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var tankerMode = peer.Capabilities.Contains("aar.tanker") && GetParticipant(peer.ParticipantId).TankerJoined;
        var ops = _operations.Values.Where(operation => !IsTerminal(operation.State) && (operation.TankerId == peer.ParticipantId || operation.ReceiverId == peer.ParticipantId))
            .Select(operation => peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation)).ToArray();
        return Result("AAR_STATE", context.OperationId, null,
            new
            {
                queue = tankerMode ? QueueView(peer.ParticipantId) : null,
                operations = ops,
                fuel = tankerMode ? FuelSummary(peer.ParticipantId) : null,
                ownPendingRequestId = _requests.Values.FirstOrDefault(request => request.ReceiverId == peer.ParticipantId && request.Status == "Pending")?.Id
            });
    }

    private void PromoteCommittedNext(ModuleCommandContext? context, string tankerId)
    {
        var next = _operations.Values.Where(operation => operation.TankerId == tankerId && operation.Slot == "CommittedNext" && operation.State == "Accepted")
            .OrderBy(operation => operation.AcceptedAt).FirstOrDefault();
        if (next is null)
        {
            var state = GetParticipant(tankerId);
            if (state.Availability == "Busy")
            {
                var peer = context?.GetPeers().FirstOrDefault(candidate => candidate.ParticipantId == tankerId) ?? state.LastIdentity;
                if (state.IsConnected && state.TankerJoined && peer is not null && peer.IsConnected && HasTankerMode(peer))
                {
                    var canOfferFuel = !state.AdapterReady || AvailableToPromise(tankerId) > 0;
                    SetAvailabilityState(context, state, canOfferFuel ? "Available" : "Unavailable");
                }
            }
            Notify(context, tankerId, "QUEUE_UPDATED", null, null, QueueView(tankerId));
            return;
        }
        next.Slot = "Active";
        next.State = "Accepted";
        next.Revision++;
        Notify(context, next.TankerId, "OPERATION_STATE", next.Id, next.Revision, OperationView(next));
        Notify(context, next.ReceiverId, "OPERATION_STATE", next.Id, next.Revision, ReceiverOperationView(next));
        Notify(context, tankerId, "QUEUE_UPDATED", null, null, QueueView(tankerId));
    }

    private void SendCurrentSnapshot(ModuleCommandContext context, string? requestId, string? operationId)
    {
        if (requestId is not null && _requests.TryGetValue(requestId, out var request))
            context.Reply("REQUEST_SNAPSHOT", request.OperationId, null, RequestViewFor(request, context.ParticipantId));
        if (operationId is not null && _operations.TryGetValue(operationId, out var operation) &&
            (operation.TankerId == context.ParticipantId || operation.ReceiverId == context.ParticipantId))
            context.Reply("OPERATION_SNAPSHOT", operation.Id, operation.Revision,
                context.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation));
    }

    private object QueueView(string tankerId) => new
    {
        active = _operations.Values.Where(operation => operation.TankerId == tankerId && operation.Slot == "Active" && !IsTerminal(operation.State)).Select(OperationView).FirstOrDefault(),
        committedNext = _operations.Values.Where(operation => operation.TankerId == tankerId && operation.Slot == "CommittedNext" && !IsTerminal(operation.State)).Select(OperationView).FirstOrDefault(),
        pending = _requests.Values.Where(request => request.TankerId == tankerId && request.Status == "Pending").OrderBy(request => request.QueueOrder).Select(RequestView).ToArray()
    };

    private object FuelSummary(string participantId)
    {
        var state = GetParticipant(participantId);
        var commitments = _operations.Values.Where(operation => operation.TankerId == participantId && operation.TransferMode == "Fuel" && !IsTerminal(operation.State))
            .Sum(operation => Math.Max(0, operation.PlannedKg - operation.TransferredKg));
        var current = state.CurrentFuelKg ?? 0;
        return new { currentFuelKg = state.CurrentFuelKg, protectedReserveKg = state.ProtectedReserveKg, committedFuelKg = commitments, availableToPromiseKg = Math.Max(0, current - state.ProtectedReserveKg - commitments) };
    }

    private double AvailableToPromise(string tankerId)
    {
        var state = GetParticipant(tankerId);
        var commitments = _operations.Values.Where(operation => operation.TankerId == tankerId && operation.TransferMode == "Fuel" && !IsTerminal(operation.State))
            .Sum(operation => Math.Max(0, operation.PlannedKg - operation.TransferredKg));
        return Math.Max(0, (state.CurrentFuelKg ?? 0) - state.ProtectedReserveKg - commitments);
    }

    private void UpdateParticipant(AarPeerSnapshot peer)
    {
        var state = GetParticipant(peer.ParticipantId);
        if (state.ClientInstanceId is { } priorInstance && priorInstance != peer.ClientInstanceId)
            FailOperations(peer.ParticipantId, "PROCESS_RESTART");
        state.ClientInstanceId = peer.ClientInstanceId;
        state.IsConnected = peer.IsConnected;
        if (state.LastIdentity is { } last && (last.UserId != peer.UserId || last.VatsimCid != peer.VatsimCid || last.Callsign != peer.Callsign || last.AircraftType != peer.AircraftType))
        {
            FailOperations(peer.ParticipantId, "IDENTITY_CHANGED");
            state.Availability = "Unavailable";
            state.TankerJoined = false;
        }
        state.LastIdentity = peer;
        if (!peer.Capabilities.Contains("aar.tanker") && state.Availability != "Off")
        {
            FailOperations(peer.ParticipantId, "CAPABILITY_LOST");
            state.Availability = "Off";
            state.TankerJoined = false;
        }
    }

    private void FailOperations(string participantId, string reason)
    {
        foreach (var operation in _operations.Values.Where(operation => !IsTerminal(operation.State) && (operation.TankerId == participantId || operation.ReceiverId == participantId)))
            FailOperation(operation, reason, promoteCommittedNext: operation.Slot == "Active" && operation.TankerId != participantId);
    }

    private void FailOperation(AarOperation operation, string reason, bool promoteCommittedNext = false)
    {
        var wasActive = operation.Slot == "Active";
        operation.State = "Failed";
        operation.Slot = "Terminal";
        operation.Revision++;
        operation.FuelOnAuthorized = false;
        operation.ClearanceValid = false;
        operation.PendingTransfer = null;
        operation.CancelledTransfer = null;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.ReconnectDeadline = null;
        operation.TerminalAt = _clock.GetUtcNow();
        _requests[operation.RequestId].Status = "Failed";
        _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        Publish(operation.TankerId, "OPERATION_FAILED", operation.Id, operation.Revision, new { operation = OperationView(operation), reason });
        Publish(operation.ReceiverId, "OPERATION_FAILED", operation.Id, operation.Revision, new { operation = ReceiverOperationView(operation), reason });
        if (wasActive && promoteCommittedNext) PromoteCommittedNext(null, operation.TankerId);
    }

    private void Suspend(AarOperation operation, string reason, TimeSpan? grace = null)
    {
        if (operation.State == "Suspended" || IsTerminal(operation.State)) return;
        PreserveCancelledTransfer(operation, "Suspended");
        operation.SuspendedFromState = operation.State;
        operation.State = "Suspended";
        operation.ClearanceValid = false;
        operation.FuelOnAuthorized = false;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.PendingTransfer = null;
        GetParticipant(operation.TankerId).Poses.Clear();
        GetParticipant(operation.ReceiverId).Poses.Clear();
        operation.ReconnectDeadline = _clock.GetUtcNow() + (grace ?? TimeSpan.FromSeconds(12));
        operation.Revision++;
        Publish(operation.TankerId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = OperationView(operation), reason, reconnectDeadline = operation.ReconnectDeadline });
        Publish(operation.ReceiverId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = ReceiverOperationView(operation), reason });
    }

    private void PreserveCancelledTransfer(AarOperation operation, string safeState)
    {
        if (operation.PendingTransfer is not { } pending) return;
        operation.CancelledTransfer ??= new CancelledTransferSettlement(pending, safeState, _clock.GetUtcNow() + TimeSpan.FromSeconds(8));
        operation.PendingTransfer = null;
    }

    private void ExpireCancelledTransferSettlement(AarOperation operation, CancelledTransferSettlement settlement)
    {
        if (settlement.TimedOut) return;
        settlement.TimedOut = true;
        if (IsTerminal(operation.State))
        {
            operation.FuelOnAuthorized = false;
            return;
        }
        operation.SuspendedFromState = settlement.SafeState;
        operation.State = "Suspended";
        operation.ClearanceValid = false;
        operation.FuelOnAuthorized = false;
        operation.PendingTransfer = null;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.ReconnectDeadline = null;
        operation.Revision++;
        Publish(operation.TankerId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = OperationView(operation), reason = "TRANSFER_SETTLEMENT_TIMEOUT" });
        Publish(operation.ReceiverId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = ReceiverOperationView(operation), reason = "TRANSFER_SETTLEMENT_TIMEOUT" });
    }

    private void Publish(string participantId, string kind, string? operationId, long? revision, object payload) =>
        EventReady?.Invoke(new AarServerEvent(participantId, kind, operationId, revision, payload));

    private async Task LifecycleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ProcessLifecycleTick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal void ProcessLifecycleTick()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var operation in _operations.Values.Where(item => item.CancelledTransfer is { TimedOut: false } settlement && settlement.Deadline <= now).ToArray())
                ExpireCancelledTransferSettlement(operation, operation.CancelledTransfer!);
            foreach (var operation in _operations.Values.Where(item => item.State == "Suspended" && item.ReconnectDeadline <= now).ToArray())
                FailOperation(operation, "RECONNECT_GRACE_EXPIRED", promoteCommittedNext: operation.Slot == "Active" && !operation.TankerReconnectRequired);
            foreach (var operation in _operations.Values.Where(item => item.State is "Contact" or "Refueling").ToArray())
            {
                var tanker = GetParticipant(operation.TankerId);
                var receiver = GetParticipant(operation.ReceiverId);
                var fuelStale = operation.TransferMode == "Fuel" &&
                    (now - tanker.FuelUpdatedAt > TimeSpan.FromSeconds(5) || now - receiver.FuelUpdatedAt > TimeSpan.FromSeconds(5));
                var poseStale = tanker.Poses.Count == 0 || receiver.Poses.Count == 0 ||
                    now - tanker.Poses.Last().TimestampUtc > _contactConfiguration.EffectiveMaximumPoseAge ||
                    now - receiver.Poses.Last().TimestampUtc > _contactConfiguration.EffectiveMaximumPoseAge;
                if (fuelStale) Suspend(operation, "FUEL_STATUS_TIMEOUT", TimeSpan.FromSeconds(5));
                else if (poseStale) Suspend(operation, "POSE_STALE");
                else if (operation.State == "Refueling" && operation.FuelOnAuthorized && operation.TransferMode == "Fuel") ProposeTransfer(operation, now);
            }
            foreach (var participant in _participants.Where(pair => pair.Value.ReconnectDeadline <= now).ToArray())
            {
                foreach (var request in _requests.Values.Where(item => item.Status == "Pending" && (item.TankerId == participant.Key || item.ReceiverId == participant.Key)).ToArray())
                {
                    request.Status = "Cancelled";
                    request.TerminalAt = now;
                    var payload = RequestView(request);
                    Publish(request.TankerId, "REQUEST_CANCELLED", null, null, payload);
                    Publish(request.ReceiverId, "REQUEST_CANCELLED", null, null, payload);
                }
                participant.Value.ReconnectDeadline = null;
            }
            ExpireResults();
        }
    }

    private bool SameIdentity(AarPeerSnapshot left, AarPeerSnapshot right) =>
        left.UserId == right.UserId && left.VatsimCid == right.VatsimCid && left.Callsign == right.Callsign && left.AircraftType == right.AircraftType;

    private bool IsFreshPose(TacticalDisplay.Core.Models.TacticalTelemetry? telemetry) => telemetry is not null &&
        _clock.GetUtcNow() - telemetry.SampleTimestampUtc <= TimeSpan.FromSeconds(2) &&
        telemetry.HeadingDeg.HasValue && telemetry.SpeedKt.HasValue;

    private void SetAvailabilityState(ModuleCommandContext? context, ParticipantState state, string availability)
    {
        state.Availability = availability;
        var participantId = state.LastIdentity?.ParticipantId;
        if (context is not null && context.ParticipantId == participantId)
            context.SetOperationalState("tankerAvailability", availability);
        else if (participantId is not null)
            OperationalStateChangeRequested?.Invoke(new ModuleOperationalStateChange(participantId, "tankerAvailability", availability));
    }

    private bool HasNonTerminalOperation(string participantId) => _operations.Values.Any(operation => operation.TankerId == participantId && !IsTerminal(operation.State));

    private bool HasLiveReceiverWork(string receiverId) =>
        _requests.Values.Any(request => request.ReceiverId == receiverId && request.Status == "Pending") ||
        _operations.Values.Any(operation => operation.ReceiverId == receiverId && !IsTerminal(operation.State));
    private static bool HasTankerMode(AarPeerSnapshot peer) => peer.Capabilities.Contains("aar.tanker");
    private ParticipantState GetParticipant(string participantId) => _participants.GetValueOrDefault(participantId) ?? (_participants[participantId] = new ParticipantState());
    private static void RequireCapability(AarPeerSnapshot peer, string capability)
    {
        if (!peer.Capabilities.Contains(capability)) throw new InvalidOperationException("The signed aircraft registry capability does not permit this role.");
    }
    private void RequireTanker(AarPeerSnapshot peer)
    {
        RequireCapability(peer, "aar.tanker");
        if (!GetParticipant(peer.ParticipantId).TankerJoined) throw new InvalidOperationException("Join tanker mode first.");
    }
    private void EnsureFreshFuelState(ParticipantState state)
    {
        if (!state.AdapterReady || state.CurrentFuelKg is null || state.FuelCapacityKg is null || _clock.GetUtcNow() - state.FuelUpdatedAt > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("A fresh ready fuel adapter status is required.");
    }

    private void EnsureAarCompatible(string? receiverType, string? tankerType)
    {
        var snapshot = registry.Current ?? throw new InvalidOperationException("AAR registry is unavailable.");
        var receiver = ResolveProfile(snapshot, receiverType);
        var tanker = ResolveProfile(snapshot, tankerType);
        if (receiver is null || tanker is null || !receiver.CanReceive || !tanker.CanTanker)
            throw new InvalidOperationException("Both enabled aircraft profiles must permit their respective AAR roles.");
    }

    private static AarAircraftProfile? ResolveProfile(AarRegistrySnapshot snapshot, string? aircraftType)
    {
        var designator = new string((aircraftType ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return snapshot.Profiles.FirstOrDefault(profile => profile.Enabled && profile.IcaoDesignators.Contains(designator, StringComparer.Ordinal));
    }

    public const double DefaultAarLimitKgPerSecond = 10;
    public static double ResolveV016AarFlow(AarAircraftProfile tanker, AarAircraftProfile receiver)
    {
        var tankerLimit = tanker.TankerSystems
            .Where(system => system.ValueProvenance == "confirmed_aircraft_specific_value")
            .Select(system => system.MaxOffloadKgPerSecond)
            .Where(IsConfirmedPositiveLimit)
            .Select(value => value!.Value)
            .DefaultIfEmpty(DefaultAarLimitKgPerSecond)
            .Min();
        var receiverLimit = receiver.ReceiverSystems
            .Where(system => system.ValueProvenance == "confirmed_aircraft_specific_value")
            .Select(system => system.MaxReceiveKgPerSecond)
            .Where(IsConfirmedPositiveLimit)
            .Select(value => value!.Value)
            .DefaultIfEmpty(DefaultAarLimitKgPerSecond)
            .Min();
        return Math.Min(tankerLimit, receiverLimit);
    }

    private static bool IsConfirmedPositiveLimit(double? value) => value is > 0 && double.IsFinite(value.Value);

    private object OperationView(AarOperation operation)
    {
        _requests.TryGetValue(operation.RequestId, out var request);
        return new
        {
            operationId = operation.Id,
            requestId = operation.RequestId,
            requestedKg = operation.RequestedKg,
            requestMode = request is null ? "Unknown" : request.Source == "TankerAdded" ? "None" : request.RequestedKg is null ? "Full" : "Fixed",
            source = request?.Source ?? "Unknown",
            tankerParticipantId = operation.TankerId,
            receiverParticipantId = operation.ReceiverId,
            slot = operation.Slot,
            state = operation.State,
            operationRevision = operation.Revision,
            plannedKg = operation.PlannedKg,
            transferredKg = operation.TransferredKg,
            remainingKg = Math.Max(0, operation.PlannedKg - operation.TransferredKg),
            effectiveFlowKgPerSecond = operation.EffectiveFlowKgPerSecond,
            transferMode = operation.TransferMode,
            fuelOnAuthorized = operation.FuelOnAuthorized,
            registryVersion = operation.RegistryVersion,
            clearanceValid = operation.ClearanceValid,
            acceptedAt = operation.AcceptedAt
        };
    }

    private static object RequestView(AarRequest request) => new
    {
        requestId = request.Id,
        receiverParticipantId = request.ReceiverId,
        tankerParticipantId = request.TankerId,
        requestedKg = request.RequestedKg,
        requestMode = request.Source == "TankerAdded" ? "None" : request.RequestedKg is null ? "Full" : "Fixed",
        full = request.Source == "ReceiverRequest" && request.RequestedKg is null,
        source = request.Source,
        queueOrder = request.QueueOrder,
        status = request.Status,
        operationId = request.OperationId,
        requestedAt = request.RequestedAt
    };

    private static object RequestViewFor(AarRequest request, string participantId) =>
        request.ReceiverId == participantId && request.Status != "Pending"
            ? new { requestId = request.Id, status = request.Status, operationId = request.OperationId }
            : RequestView(request);

    private static object ReceiverOperationView(AarOperation operation) => new
    {
        operationId = operation.Id,
        requestId = operation.RequestId,
        state = operation.State,
        operationRevision = operation.Revision,
        status = ReceiverStatus(operation.State),
        slot = operation.Slot,
        transferMode = operation.TransferMode
    };

    private static string ReceiverStatus(string state) => state switch
    {
        "Accepted" => "REQUEST_ACCEPTED",
        "Astern" => "CLEARED_ASTERN",
        "ClearedContact" => "CLEARED_CONTACT",
        "Contact" => "CONTACT",
        "Refueling" => "REFUELING",
        "Disconnecting" => "DISCONNECT",
        "Breakaway" => "BREAKAWAY",
        "Suspended" => "SUSPENDED",
        "Complete" => "COMPLETE",
        "Failed" => "FAILED",
        "Cancelled" => "CANCELLED",
        _ => "HOLD"
    };

    private sealed class AarLimitExceededException(double maximumKg)
        : InvalidOperationException($"Planned amount exceeds the safe maximum of {maximumKg:0.##} kg.")
    {
        public double MaximumKg { get; } = maximumKg;
    }

    private void Notify(ModuleCommandContext? context, string participantId, string kind, string? operationId, long? revision, object payload)
    {
        if (context is null) Publish(participantId, kind, operationId, revision, payload);
        else context.SendEvent(participantId, kind, operationId, revision, payload);
    }

    private static CommandResponse Result(string kind, string? operationId, long? revision, object payload, string? requestId = null) =>
        new(kind, operationId, revision, payload, requestId);

    private static string String(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{name} is required.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement payload, string name, double min, double max)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result) || result < min || result > max)
            throw new ArgumentException($"{name} must be a finite number from {min} to {max}.");
        return result;
    }

    private static bool TryNumber(JsonElement payload, string name, out double result)
    {
        result = 0;
        if (!payload.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out result) || !double.IsFinite(result) || result <= 0 || result > 1_000_000)
            throw new ArgumentException($"{name} must be a finite positive amount no greater than 1000000.");
        return true;
    }

    private static double? ParseRequestAmount(JsonElement payload)
    {
        if (!payload.TryGetProperty("full", out var full) || full.ValueKind != JsonValueKind.True && full.ValueKind != JsonValueKind.False)
            throw new ArgumentException("full is required.");
        if (full.GetBoolean()) return null;
        return Number(payload, "requestedKg", 0.001, 1_000_000);
    }

    private static bool IsTerminal(string state) => state is "Rejected" or "Cancelled" or "Breakaway" or "Complete" or "Failed";

    private static string RequestHash(ModuleCommandContext context)
    {
        var text = $"{context.Module}\n{context.Kind}\n{context.OperationId}\n{CanonicalJson(context.Payload)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }

    private bool TryGetCached(string participantId, string messageId, out CachedResult cached)
    {
        if (_results.TryGetValue(participantId, out var entries) && entries.TryGetValue(messageId, out cached!)) return true;
        if (_safetyResults.TryGetValue(participantId, out var safetyEntries) && safetyEntries.TryGetValue(messageId, out cached!)) return true;
        cached = null!;
        return false;
    }

    private void Remember(string participantId, string messageId, string requestHash, CommandResponse response, string commandKind)
    {
        var target = IsSafetyCommand(commandKind) ? _safetyResults : _results;
        if (!target.TryGetValue(participantId, out var entries)) target[participantId] = entries = new(StringComparer.Ordinal);
        entries[messageId] = new CachedResult(requestHash, response.Kind, response.OperationId, response.OperationRevision, response.Payload,
            response.RequestId, _clock.GetUtcNow());
        if (IsSafetyCommand(commandKind)) BoundSafetyResults(participantId);
    }

    private bool TryGetTransferAckHistory(string participantId, string messageId, out TransferAckHistory history)
    {
        if (_transferAckHistory.TryGetValue(participantId, out var entries) && entries.TryGetValue(messageId, out history!)) return true;
        history = null!;
        return false;
    }

    private void RememberTransferAck(string participantId, string messageId, string requestHash, string proposalId, string? operationId, CommandResponse response)
    {
        if (!_transferAckHistory.TryGetValue(participantId, out var entries)) _transferAckHistory[participantId] = entries = new(StringComparer.Ordinal);
        if (entries.ContainsKey(messageId)) return;
        while ((entries.Count >= MaxTransferAckHistoryPerParticipant || GetTransferAckHistoryCount() >= MaxTransferAckHistoryGlobal) &&
               !EvictOldestSettledTransferAck())
        {
            // Protected records are bounded by the maximum live operation count.
            // If that invariant changes, skipping an optional replay record remains
            // safe because proposal-bound stale ACKs cannot mutate another proposal.
            if (entries.Count >= MaxTransferAckHistoryPerParticipant || GetTransferAckHistoryCount() >= MaxTransferAckHistoryGlobal) return;
        }
        entries[messageId] = new TransferAckHistory(requestHash, proposalId, operationId, response, _clock.GetUtcNow());
    }

    private void ExpireResults()
    {
        var now = _clock.GetUtcNow();
        foreach (var participant in _results.Values)
            foreach (var pair in participant.Where(pair => GetExpiry(pair.Value) <= now).ToArray()) participant.Remove(pair.Key);
        foreach (var participantId in _results.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray()) _results.Remove(participantId);

        foreach (var participant in _transferAckHistory.Values)
            foreach (var pair in participant.Where(pair => IsTransferAckHistoryExpired(pair.Value, now)).ToArray()) participant.Remove(pair.Key);
        foreach (var participantId in _transferAckHistory.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray()) _transferAckHistory.Remove(participantId);
        foreach (var participant in _safetyResults.Values)
            foreach (var pair in participant.Where(pair => GetExpiry(pair.Value) <= now).ToArray()) participant.Remove(pair.Key);
        foreach (var participantId in _safetyResults.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray()) _safetyResults.Remove(participantId);

        foreach (var operation in _operations.Values.Where(operation => IsTerminal(operation.State) &&
                     operation.TerminalAt is { } terminalAt && terminalAt + CompletedResultTtl <= now &&
                     (operation.CancelledTransfer is null || operation.CancelledTransfer.Deadline <= now)).ToArray())
        {
            _operations.Remove(operation.Id);
            foreach (var participant in _participants.Values.Where(state => state.LastAppliedOperationId == operation.Id))
            {
                participant.LastAppliedOperationId = null;
                participant.LastAppliedTransferredKg = null;
            }
        }

        foreach (var request in _requests.Values.Where(request => request.Status != "Pending" && request.TerminalAt is { } terminalAt && terminalAt + CompletedResultTtl <= now &&
                     (request.OperationId is null || !_operations.ContainsKey(request.OperationId))).ToArray())
            _requests.Remove(request.Id);
    }

    private int GetParticipantEntryCount(string participantId) => _results.TryGetValue(participantId, out var entries) ? entries.Count : 0;

    private int GetGlobalEntryCount() => _results.Values.Sum(entries => entries.Count);

    private DateTimeOffset GetExpiry(CachedResult entry)
    {
        if (entry.RequestId is { } requestId && _requests.TryGetValue(requestId, out var request))
        {
            if (request.Status == "Pending") return DateTimeOffset.MaxValue;
            if (request.OperationId is { } linkedOperation && _operations.TryGetValue(linkedOperation, out var linked) && !IsTerminal(linked.State))
                return DateTimeOffset.MaxValue;
            if (request.TerminalAt is { } requestTerminal) return requestTerminal + CompletedResultTtl;
        }
        if (entry.OperationId is { } operationId && _operations.TryGetValue(operationId, out var operation))
            return IsTerminal(operation.State) ? (operation.TerminalAt ?? entry.CreatedAt) + CompletedResultTtl : DateTimeOffset.MaxValue;
        return entry.CreatedAt + CompletedResultTtl;
    }

    private bool IsTransferAckHistoryExpired(TransferAckHistory entry, DateTimeOffset now)
    {
        if (entry.OperationId is { } operationId && _operations.TryGetValue(operationId, out var operation))
            if (operation.PendingTransfer?.Id == entry.ProposalId || operation.CancelledTransfer?.Proposal.Id == entry.ProposalId) return false;
        return entry.CreatedAt + TransferAckReplayTtl <= now;
    }

    private bool IsTransferAckProtected(TransferAckHistory entry) =>
        entry.OperationId is { } operationId && _operations.TryGetValue(operationId, out var operation) &&
        (operation.PendingTransfer?.Id == entry.ProposalId || operation.CancelledTransfer?.Proposal.Id == entry.ProposalId);

    private bool EvictOldestSettledTransferAck()
    {
        var candidate = _transferAckHistory
            .SelectMany(participant => participant.Value.Select(entry => (ParticipantId: participant.Key, MessageId: entry.Key, Entry: entry.Value)))
            .Where(item => !IsTransferAckProtected(item.Entry))
            .OrderBy(item => item.Entry.CreatedAt)
            .FirstOrDefault();
        if (candidate.Entry is null) return false;
        _transferAckHistory[candidate.ParticipantId].Remove(candidate.MessageId);
        return true;
    }

    private int GetTransferAckHistoryCount() => _transferAckHistory.Values.Sum(entries => entries.Count);

    private void BoundSafetyResults(string participantId)
    {
        while ((_safetyResults.TryGetValue(participantId, out var entries) && entries.Count > MaxSafetyResultsPerParticipant) ||
               _safetyResults.Values.Sum(items => items.Count) > MaxSafetyResultsGlobal)
        {
            var candidate = _safetyResults.SelectMany(participant => participant.Value.Select(entry =>
                    (ParticipantId: participant.Key, MessageId: entry.Key, Entry: entry.Value)))
                .OrderBy(item => item.Entry.CreatedAt).First();
            _safetyResults[candidate.ParticipantId].Remove(candidate.MessageId);
            if (_safetyResults[candidate.ParticipantId].Count == 0) _safetyResults.Remove(candidate.ParticipantId);
        }
    }

    private static bool IsSafetyCommand(string kind) => kind is "STOP_TRANSFER" or "HOLD" or "BREAKAWAY" or "DISCONNECT";

    private static bool HasProposalBoundAckId(ModuleCommandContext context, string messageId) =>
        TryGetString(context.Payload, "proposalId", out var proposalId) && messageId == "transfer-ack-" + proposalId;

    private static bool TryGetString(JsonElement payload, string name, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(node.GetString()))
        {
            value = node.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool RequiresIdempotencyCache(string kind) => kind is not ("PING" or "GET_STATE" or "FUEL_STATUS" or "POSE_UPDATE");

    private sealed class ParticipantState
    {
        public bool TankerJoined { get; set; }
        public bool ReserveSet { get; set; }
        public double ProtectedReserveKg { get; set; }
        public string Availability { get; set; } = "Off";
        public bool AdapterReady { get; set; }
        public double? CurrentFuelKg { get; set; }
        public double? FuelCapacityKg { get; set; }
        public DateTimeOffset FuelUpdatedAt { get; set; }
        public AarPeerSnapshot? LastIdentity { get; set; }
        public string? ClientInstanceId { get; set; }
        public bool IsConnected { get; set; }
        public DateTimeOffset? DisconnectedAt { get; set; }
        public DateTimeOffset? ReconnectDeadline { get; set; }
        public double? LastAppliedTransferredKg { get; set; }
        public string? LastAppliedOperationId { get; set; }
        public Queue<AarPose> Poses { get; } = new();
    }

    private sealed class AarRequest(string id, string receiverId, string tankerId, double? requestedKg, DateTimeOffset requestedAt, long queueOrder)
    {
        public string Id { get; } = id;
        public string ReceiverId { get; } = receiverId;
        public string TankerId { get; } = tankerId;
        public double? RequestedKg { get; } = requestedKg;
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public long QueueOrder { get; set; } = queueOrder;
        public string Source { get; set; } = "ReceiverRequest";
        public string TransferMode { get; set; } = "Fuel";
        public string Status { get; set; } = "Pending";
        public string? OperationId { get; set; }
        public DateTimeOffset? TerminalAt { get; set; }
    }

    private sealed class AarOperation(string id, string requestId, string tankerId, string receiverId,
        string? tankerClientInstanceId, string? receiverClientInstanceId, long tankerGeneration, long receiverGeneration,
        string slot, string state, long revision, double? requestedKg, double plannedKg, double transferredKg, double effectiveFlowKgPerSecond, int registryVersion,
        AarAircraftProfile tankerProfile, AarAircraftProfile receiverProfile, AarPeerSnapshot tankerIdentity, AarPeerSnapshot receiverIdentity, DateTimeOffset acceptedAt)
    {
        public string Id { get; } = id;
        public string RequestId { get; } = requestId;
        public string TankerId { get; } = tankerId;
        public string ReceiverId { get; } = receiverId;
        public string? TankerClientInstanceId { get; } = tankerClientInstanceId;
        public string? ReceiverClientInstanceId { get; } = receiverClientInstanceId;
        public long TankerGeneration { get; } = tankerGeneration;
        public long ReceiverGeneration { get; } = receiverGeneration;
        public AarPeerSnapshot TankerIdentity { get; } = tankerIdentity;
        public AarPeerSnapshot ReceiverIdentity { get; } = receiverIdentity;
        public string Slot { get; set; } = slot;
        public string State { get; set; } = state;
        public long Revision { get; set; } = revision;
        public double? RequestedKg { get; } = requestedKg;
        public double PlannedKg { get; set; } = plannedKg;
        public double TransferredKg { get; set; } = transferredKg;
        public double EffectiveFlowKgPerSecond { get; } = effectiveFlowKgPerSecond;
        public int RegistryVersion { get; } = registryVersion;
        public AarAircraftProfile TankerProfile { get; } = tankerProfile;
        public AarAircraftProfile ReceiverProfile { get; } = receiverProfile;
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public bool ClearanceValid { get; set; }
        public bool FuelOnAuthorized { get; set; }
        public string TransferMode { get; set; } = "Fuel";
        public DateTimeOffset? TerminalAt { get; set; }
        public string? SuspendedFromState { get; set; }
        public DateTimeOffset? ReconnectDeadline { get; set; }
        public bool TankerReconnectRequired { get; set; }
        public bool ReceiverReconnectRequired { get; set; }
        public DateTimeOffset? CaptureSince { get; set; }
        public DateTimeOffset? ContactEstablishedAt { get; set; }
        public DateTimeOffset? ReleaseSince { get; set; }
        public DateTimeOffset LastTransferProposalAt { get; set; } = DateTimeOffset.MinValue;
        public AarTransferProposal? PendingTransfer { get; set; }
        public CancelledTransferSettlement? CancelledTransfer { get; set; }
    }

    private sealed class AarTransferProposal(string id, double deltaKg, double targetCumulativeKg, DateTimeOffset createdAt, long revision)
    {
        public string Id { get; } = id;
        public double DeltaKg { get; } = deltaKg;
        public double TargetCumulativeKg { get; } = targetCumulativeKg;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public long Revision { get; } = revision;
        public double? TankerAppliedKg { get; set; }
        public double? ReceiverAppliedKg { get; set; }
    }

    private sealed class CancelledTransferSettlement(AarTransferProposal proposal, string safeState, DateTimeOffset deadline)
    {
        public AarTransferProposal Proposal { get; } = proposal;
        public string SafeState { get; } = safeState;
        public DateTimeOffset Deadline { get; } = deadline;
        public bool TimedOut { get; set; }
        public double? TankerAppliedKg { get; set; }
        public double? ReceiverAppliedKg { get; set; }
    }

    private sealed record CommandResponse(string Kind, string? OperationId, long? OperationRevision, object Payload, string? RequestId = null);
    private sealed record CachedResult(string RequestHash, string Kind, string? OperationId, long? OperationRevision, object Payload,
        string? RequestId, DateTimeOffset CreatedAt);
    private sealed record TransferAckHistory(string RequestHash, string ProposalId, string? OperationId, CommandResponse Response, DateTimeOffset CreatedAt);
}
