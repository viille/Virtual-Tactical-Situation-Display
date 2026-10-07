using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TacticalLink.Server.Modules;

namespace TacticalLink.Server.Aar;

public sealed record AarServerEvent(string ParticipantId, string Kind, string? OperationId, long? OperationRevision, object Payload);

public sealed class AarModuleHandler(IAarRegistryProvider registry, TimeProvider? timeProvider = null, AarContactConfiguration? contactConfiguration = null) : IModuleMessageHandler, IHostedService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CompletedResultTtl = TimeSpan.FromHours(24);
    private const int MaxResultsPerParticipant = 256;
    private const int MaxResultsGlobal = 32_768;
    private const int MaxPendingPerTanker = 8;
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AarContactConfiguration _contactConfiguration = contactConfiguration ?? new AarContactConfiguration();
    private readonly Dictionary<string, ParticipantState> _participants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AarRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AarOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, CachedResult>> _results = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifecycleCts;
    private Task? _lifecycleTask;

    public string Module => "aar";
    public int ProtocolVersion => 1;
    public event Action<AarServerEvent>? EventReady;
    internal int RequestCountForTests { get { lock (_gate) return _requests.Count; } }

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

            if (RequiresIdempotencyCache(context.Kind) && (GetParticipantEntryCount(context.ParticipantId) >= MaxResultsPerParticipant || GetGlobalEntryCount() >= MaxResultsGlobal))
            {
                context.Reply("MODULE_ERROR", context.OperationId, null, new { messageId = key, code = "IDEMPOTENCY_CAPACITY", message = "The bounded command result cache is full; retry after expired entries are removed." });
                return Task.CompletedTask;
            }

            var response = Execute(context, peer);
            if (context.Kind != "POSE_UPDATE" || response.Kind == "MODULE_ERROR")
                context.Reply(response.Kind, response.OperationId, response.OperationRevision, response.Payload);
            if (RequiresIdempotencyCache(context.Kind)) Remember(context.ParticipantId, key, requestHash, response);
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
                "PROCEED_TO_PRECONTACT" => ChangeActiveState(context, peer, "PreContact", "PRECONTACT_STARTED"),
                "REQUEST_REFUEL" => RequestRefuel(context, peer),
                "ACCEPT_REQUEST" => AcceptRequest(context, peer),
                "REJECT_REQUEST" => RejectRequest(context, peer),
                "CANCEL_REQUEST" => CancelRequest(context, peer),
                "CANCEL_COMMITMENT" => CancelCommitment(context, peer),
                "CLEAR_CONTACT" => ChangeActiveState(context, peer, "ClearedContact", "CLEAR_CONTACTED"),
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
        if (requested == "Available" && (!state.ReserveSet || !state.AdapterReady || state.CurrentFuelKg is null || state.FuelCapacityKg is null))
            throw new InvalidOperationException("A protected reserve and ready fuel adapter are required before becoming Available.");
        if (requested == "Available" && AvailableToPromise(peer.ParticipantId) <= 0)
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
        state.LastAppliedTransferredKg = context.Payload.TryGetProperty("lastAppliedTransferredKg", out var watermark) && watermark.ValueKind == JsonValueKind.Number && watermark.TryGetDouble(out var applied) && double.IsFinite(applied) && applied >= 0
            ? applied
            : null;
        if (!ready && state.Availability == "Available") SetAvailabilityState(context, state, "Unavailable");
        if (!ready)
            foreach (var operation in _operations.Values.Where(item => !IsTerminal(item.State) && (item.TankerId == peer.ParticipantId || item.ReceiverId == peer.ParticipantId)))
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
            var commonTime = tankerHistory.Last().TimestampUtc > receiverHistory.Last().TimestampUtc
                ? tankerHistory.Last().TimestampUtc
                : receiverHistory.Last().TimestampUtc;
            var tankerPose = Nearest(tankerHistory, commonTime);
            var receiverPose = Nearest(receiverHistory, commonTime);
            if (tankerPose is null || receiverPose is null ||
                !AarContactGeometry.TryMeasure(tankerPose, receiverPose, _contactConfiguration, _clock.GetUtcNow(), out var relative))
                continue;

            var capture = AarContactGeometry.IsInsideCapture(relative, _contactConfiguration);
            var release = AarContactGeometry.IsInsideRelease(relative, _contactConfiguration);
            var now = _clock.GetUtcNow();
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
                if (release)
                {
                    operation.State = "Refueling";
                    operation.Revision++;
                    Publish(operation.TankerId, "REFUELING_STARTED", operation.Id, operation.Revision, OperationView(operation));
                    Publish(operation.ReceiverId, "REFUELING_STARTED", operation.Id, operation.Revision, ReceiverOperationView(operation));
                }
                else if (capture) operation.ReleaseSince = null;
                else operation.ReleaseSince ??= now;
            }
            else if (operation.State == "Refueling")
            {
                operation.ReleaseSince = release ? null : operation.ReleaseSince ?? now;
                if (operation.ReleaseSince is { } released && now - released >= _contactConfiguration.EffectiveReleaseDebounce)
                    CompleteOperation(context, operation, "CONTACT_RELEASED");
            }
        }
        _ = sampleTime;
    }

    private CommandResponse AcknowledgeTransfer(ModuleCommandContext context, AarPeerSnapshot peer)
    {
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.State != "Refueling" || operation.PendingTransfer is null)
            throw new InvalidOperationException("There is no pending transfer proposal to acknowledge.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this operation.");
        var proposalId = String(context.Payload, "proposalId");
        var applied = Number(context.Payload, "appliedCumulativeKg", 0, operation.PlannedKg);
        var pending = operation.PendingTransfer;
        if (proposalId != pending.Id || Math.Abs(applied - pending.TargetCumulativeKg) > 0.05)
        {
            Suspend(operation, "TRANSFER_ACK_MISMATCH", TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Transfer acknowledgement did not match the outstanding proposal.");
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
            if (operation.TransferredKg >= operation.PlannedKg - 0.01) CompleteOperation(context, operation, "TRANSFER_COMPLETE");
        }
        return Result("TRANSFER_ACK_ACCEPTED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation));
    }

    private void ProposeTransfer(AarOperation operation, DateTimeOffset now)
    {
        if (operation.PendingTransfer is not null)
        {
            if (now - operation.PendingTransfer.CreatedAt > TimeSpan.FromSeconds(3))
                Suspend(operation, "TRANSFER_ACK_TIMEOUT", TimeSpan.FromSeconds(5));
            return;
        }
        if (now - operation.LastTransferProposalAt < TimeSpan.FromSeconds(1)) return;
        var amount = Math.Min(operation.EffectiveFlowKgPerSecond, operation.PlannedKg - operation.TransferredKg);
        if (amount <= 0) return;
        var proposal = new AarTransferProposal(Guid.NewGuid().ToString("N"), operation.TransferredKg + amount, now);
        operation.PendingTransfer = proposal;
        operation.Revision++;
        var tankerPayload = new { proposalId = proposal.Id, deltaKg = amount, targetCumulativeKg = proposal.TargetCumulativeKg };
        Publish(operation.TankerId, "TRANSFER_PROPOSAL", operation.Id, operation.Revision, tankerPayload);
        Publish(operation.ReceiverId, "TRANSFER_PROPOSAL", operation.Id, operation.Revision,
            new { operationId = operation.Id, status = "TRANSFER_IN_PROGRESS", proposalId = proposal.Id });
    }

    private AarPose? Nearest(Queue<AarPose> poses, DateTimeOffset timestamp) =>
        poses.OrderBy(pose => Math.Abs((pose.TimestampUtc - timestamp).TotalMilliseconds)).FirstOrDefault(pose =>
            Math.Abs((pose.TimestampUtc - timestamp).TotalMilliseconds) <= _contactConfiguration.EffectiveMaximumAlignmentGap.TotalMilliseconds);

    private void CompleteOperation(ModuleCommandContext context, AarOperation operation, string terminalKind)
    {
        operation.State = "Disconnecting";
        operation.Revision++;
        operation.ClearanceValid = false;
        Publish(operation.TankerId, "OPERATION_DISCONNECTING", operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, "OPERATION_DISCONNECTING", operation.Id, operation.Revision, ReceiverOperationView(operation));
        operation.State = "Complete";
        operation.Slot = "Terminal";
        operation.TerminalAt = _clock.GetUtcNow();
        _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        operation.Revision++;
        Publish(operation.TankerId, terminalKind, operation.Id, operation.Revision, OperationView(operation));
        Publish(operation.ReceiverId, terminalKind, operation.Id, operation.Revision, ReceiverOperationView(operation));
        PromoteCommittedNext(context, operation.TankerId);
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
        EnsureFreshFuelState(receiverState);
        EnsureFreshFuelState(tankerState);
        if (!tankerState.TankerJoined || tankerState.Availability is not ("Available" or "Busy")) throw new InvalidOperationException("The selected tanker is not Available.");
        var requestAmount = ParseRequestAmount(context.Payload);
        EnsureBoomCompatible(peer.AircraftType, tanker.AircraftType);
        if (_requests.Values.Any(request => request.ReceiverId == peer.ParticipantId && request.TankerId == tankerId && request.Status == "Pending"))
            throw new InvalidOperationException("This receiver already has a pending request to that tanker.");
        var pending = _requests.Values.Count(request => request.TankerId == tankerId && request.Status == "Pending");
        if (pending >= MaxPendingPerTanker) throw new InvalidOperationException("The tanker pending request queue is full.");
        var id = "req_" + Guid.NewGuid().ToString("N");
        var item = new AarRequest(id, peer.ParticipantId, tankerId, requestAmount, _clock.GetUtcNow());
        _requests.Add(id, item);
        Notify(context, tankerId, "REQUEST_QUEUED", null, null, RequestView(item));
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
        EnsureFreshFuelState(tankerState);
        EnsureFreshFuelState(receiverState);
        if (!tankerState.TankerJoined || tankerState.Availability is not ("Available" or "Busy")) throw new InvalidOperationException("The tanker is not available for acceptance.");
        if (!tankerState.AdapterReady || !receiverState.AdapterReady) throw new InvalidOperationException("Both fuel adapters must be ready.");
        EnsureBoomCompatible(receiver.AircraftType, peer.AircraftType);
        var tankerOps = _operations.Values.Where(operation => operation.TankerId == peer.ParticipantId && !IsTerminal(operation.State)).ToArray();
        if (tankerOps.Count(operation => operation.Slot == "Active") >= 1 && tankerOps.Any(operation => operation.Slot == "CommittedNext"))
            throw new InvalidOperationException("The CommittedNext slot is occupied; the request remains Pending.");
        var slot = tankerOps.Any(operation => operation.Slot == "Active") ? "CommittedNext" : "Active";
        var committed = tankerOps.Sum(operation => Math.Max(0, operation.PlannedKg - operation.TransferredKg));
        var available = Math.Max(0, tankerState.CurrentFuelKg!.Value - tankerState.ProtectedReserveKg - committed);
        var receiverFree = Math.Max(0, receiverState.FuelCapacityKg!.Value - receiverState.CurrentFuelKg!.Value);
        var desired = request.RequestedKg ?? receiverFree;
        var planned = Math.Min(desired, Math.Min(receiverFree, available));
        if (planned <= 0) throw new InvalidOperationException("No safe positive fuel commitment is available; request remains Pending.");
        var registrySnapshot = registry.Current ?? throw new InvalidOperationException("AAR registry is unavailable.");
        var tankerProfile = ResolveProfile(registrySnapshot, peer.AircraftType)!;
        var receiverProfile = ResolveProfile(registrySnapshot, receiver.AircraftType)!;
        var flow = EffectiveBoomFlow(tankerProfile, receiverProfile);
        var operation = new AarOperation(
            "aar_" + Guid.NewGuid().ToString("N"), request.Id, peer.ParticipantId, receiver.ParticipantId,
            peer.ClientInstanceId, receiver.ClientInstanceId, peer.ConnectionGeneration, receiver.ConnectionGeneration,
            slot, "Accepted", 1, planned, 0, flow, registrySnapshot.Version, tankerProfile, receiverProfile, peer, receiver, _clock.GetUtcNow());
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
        return Result("REQUEST_CANCELLED", null, null, RequestView(request), request.Id);
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
        return Result("OPERATION_CANCELLED", operation.Id, operation.Revision,
            peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation), operation.RequestId);
    }

    private CommandResponse ChangeActiveState(ModuleCommandContext context, AarPeerSnapshot peer, string target, string eventKind)
    {
        var operationId = context.OperationId ?? String(context.Payload, "operationId");
        if (!_operations.TryGetValue(operationId, out var operation) || operation.Slot != "Active" || IsTerminal(operation.State))
            throw new InvalidOperationException("No active operation matches this command.");
        if (peer.ParticipantId != operation.TankerId && peer.ParticipantId != operation.ReceiverId)
            throw new InvalidOperationException("The participant does not own this operation.");
        if (target == "PreContact")
        {
            if (peer.ParticipantId != operation.TankerId || operation.State != "Accepted") throw new InvalidOperationException("Only the tanker can start PreContact from Accepted.");
        }
        else if (target == "ClearedContact")
        {
            if (peer.ParticipantId != operation.TankerId || operation.State != "PreContact") throw new InvalidOperationException("Only the tanker can clear contact from PreContact.");
            operation.ClearanceValid = true;
        }
        else if (target == "Complete")
        {
            if (operation.State is not ("Accepted" or "PreContact" or "ClearedContact" or "Contact" or "Refueling"))
                throw new InvalidOperationException("The active operation cannot disconnect from its current state.");
            operation.State = "Disconnecting";
            operation.Revision++;
            Notify(context, operation.TankerId, "OPERATION_STATE", operation.Id, operation.Revision, OperationView(operation));
            Notify(context, operation.ReceiverId, "OPERATION_STATE", operation.Id, operation.Revision, ReceiverOperationView(operation));
            target = "Complete";
        }
        operation.State = target;
        operation.Revision++;
        if (target is "Breakaway" or "Complete") operation.ClearanceValid = false;
        if (target is "Breakaway" or "Complete")
        {
            operation.Slot = "Terminal";
            operation.TerminalAt = _clock.GetUtcNow();
            _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
            PromoteCommittedNext(context, operation.TankerId);
        }
        Notify(context, operation.TankerId, eventKind, operation.Id, operation.Revision, OperationView(operation));
        Notify(context, operation.ReceiverId, eventKind, operation.Id, operation.Revision, ReceiverOperationView(operation));
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
            FailOperation(operation, "IDENTITY_OR_PROCESS_CHANGED");
            throw new InvalidOperationException("Participant identity or process changed; operation failed closed.");
        }
        EnsureFreshFuelState(tankerState);
        EnsureFreshFuelState(receiverState);
        if (!IsFreshPose(tanker.Telemetry) || !IsFreshPose(receiver.Telemetry)) throw new InvalidOperationException("Fresh pose is required before reconciliation.");
        if (tankerState.LastAppliedTransferredKg is not { } tankerApplied || receiverState.LastAppliedTransferredKg is not { } receiverApplied ||
            Math.Abs(tankerApplied - receiverApplied) > 0.05 || tankerApplied + 0.05 < operation.TransferredKg || tankerApplied > operation.PlannedKg + 0.05)
        {
            FailOperation(operation, "FUEL_STATE_UNRECONCILABLE");
            throw new InvalidOperationException("Fuel application watermarks disagree or are outside the accepted plan.");
        }

        operation.TransferredKg = Math.Max(operation.TransferredKg, Math.Min(tankerApplied, receiverApplied));
        operation.ClearanceValid = false;
        operation.ReconnectDeadline = null;
        operation.TankerReconnectRequired = false;
        operation.ReceiverReconnectRequired = false;
        if (operation.SuspendedFromState == "Disconnecting")
        {
            operation.State = "Complete";
            operation.Slot = "Terminal";
            operation.TerminalAt = _clock.GetUtcNow();
            _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        }
        else if (operation.Slot == "CommittedNext") operation.State = "Accepted";
        else operation.State = "PreContact";
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
        var ops = _operations.Values.Where(operation => operation.TankerId == peer.ParticipantId || operation.ReceiverId == peer.ParticipantId)
            .Select(operation => peer.ParticipantId == operation.TankerId ? OperationView(operation) : ReceiverOperationView(operation)).ToArray();
        return Result("AAR_STATE", context.OperationId, null,
            new { queue = tankerMode ? QueueView(peer.ParticipantId) : null, operations = ops, fuel = tankerMode ? FuelSummary(peer.ParticipantId) : null });
    }

    private void PromoteCommittedNext(ModuleCommandContext context, string tankerId)
    {
        var next = _operations.Values.Where(operation => operation.TankerId == tankerId && operation.Slot == "CommittedNext" && operation.State == "Accepted")
            .OrderBy(operation => operation.AcceptedAt).FirstOrDefault();
        if (next is null)
        {
            var state = GetParticipant(tankerId);
            if (state.Availability == "Busy")
            {
                var peer = context.GetPeers().FirstOrDefault(candidate => candidate.ParticipantId == tankerId);
                if (peer is not null && HasTankerMode(peer)) SetAvailabilityState(context, state, "Available");
            }
            return;
        }
        next.Slot = "Active";
        next.State = "PreContact";
        next.Revision++;
        Notify(context, next.TankerId, "OPERATION_STATE", next.Id, next.Revision, OperationView(next));
        Notify(context, next.ReceiverId, "OPERATION_STATE", next.Id, next.Revision, ReceiverOperationView(next));
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
        pending = _requests.Values.Where(request => request.TankerId == tankerId && request.Status == "Pending").OrderBy(request => request.RequestedAt).Select(RequestView).ToArray()
    };

    private object FuelSummary(string participantId)
    {
        var state = GetParticipant(participantId);
        var commitments = _operations.Values.Where(operation => operation.TankerId == participantId && !IsTerminal(operation.State))
            .Sum(operation => Math.Max(0, operation.PlannedKg - operation.TransferredKg));
        var current = state.CurrentFuelKg ?? 0;
        return new { currentFuelKg = state.CurrentFuelKg, protectedReserveKg = state.ProtectedReserveKg, committedFuelKg = commitments, availableToPromiseKg = Math.Max(0, current - state.ProtectedReserveKg - commitments) };
    }

    private double AvailableToPromise(string tankerId)
    {
        var state = GetParticipant(tankerId);
        var commitments = _operations.Values.Where(operation => operation.TankerId == tankerId && !IsTerminal(operation.State))
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
            FailOperation(operation, reason);
    }

    private void FailOperation(AarOperation operation, string reason)
    {
        operation.State = "Failed";
        operation.Slot = "Terminal";
        operation.Revision++;
        operation.ClearanceValid = false;
        operation.TerminalAt = _clock.GetUtcNow();
        _requests[operation.RequestId].TerminalAt = operation.TerminalAt;
        Publish(operation.TankerId, "OPERATION_FAILED", operation.Id, operation.Revision, new { operation = OperationView(operation), reason });
        Publish(operation.ReceiverId, "OPERATION_FAILED", operation.Id, operation.Revision, new { operation = ReceiverOperationView(operation), reason });
    }

    private void Suspend(AarOperation operation, string reason, TimeSpan? grace = null)
    {
        if (operation.State == "Suspended" || IsTerminal(operation.State)) return;
        operation.SuspendedFromState = operation.State;
        operation.State = "Suspended";
        operation.ClearanceValid = false;
        operation.CaptureSince = null;
        operation.ReleaseSince = null;
        operation.PendingTransfer = null;
        GetParticipant(operation.TankerId).Poses.Clear();
        GetParticipant(operation.ReceiverId).Poses.Clear();
        GetParticipant(operation.TankerId).LastAppliedTransferredKg = null;
        GetParticipant(operation.ReceiverId).LastAppliedTransferredKg = null;
        operation.ReconnectDeadline = _clock.GetUtcNow() + (grace ?? TimeSpan.FromSeconds(12));
        operation.Revision++;
        Publish(operation.TankerId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = OperationView(operation), reason, reconnectDeadline = operation.ReconnectDeadline });
        Publish(operation.ReceiverId, "OPERATION_SUSPENDED", operation.Id, operation.Revision,
            new { operation = ReceiverOperationView(operation), reason });
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
                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    foreach (var operation in _operations.Values.Where(item => item.State == "Suspended" && item.ReconnectDeadline <= now).ToArray())
                        FailOperation(operation, "RECONNECT_GRACE_EXPIRED");
                    foreach (var operation in _operations.Values.Where(item => item.State is "Contact" or "Refueling").ToArray())
                    {
                        var tanker = GetParticipant(operation.TankerId);
                        var receiver = GetParticipant(operation.ReceiverId);
                        var fuelStale = now - tanker.FuelUpdatedAt > TimeSpan.FromSeconds(5) || now - receiver.FuelUpdatedAt > TimeSpan.FromSeconds(5);
                        var poseStale = tanker.Poses.Count == 0 || receiver.Poses.Count == 0 ||
                            now - tanker.Poses.Last().TimestampUtc > _contactConfiguration.EffectiveMaximumPoseAge ||
                            now - receiver.Poses.Last().TimestampUtc > _contactConfiguration.EffectiveMaximumPoseAge;
                        if (fuelStale) Suspend(operation, "FUEL_STATUS_TIMEOUT", TimeSpan.FromSeconds(5));
                        else if (poseStale) Suspend(operation, "POSE_STALE");
                        else if (operation.State == "Refueling") ProposeTransfer(operation, now);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private bool SameIdentity(AarPeerSnapshot left, AarPeerSnapshot right) =>
        left.UserId == right.UserId && left.VatsimCid == right.VatsimCid && left.Callsign == right.Callsign && left.AircraftType == right.AircraftType;

    private bool IsFreshPose(TacticalDisplay.Core.Models.TacticalTelemetry? telemetry) => telemetry is not null &&
        _clock.GetUtcNow() - telemetry.SampleTimestampUtc <= TimeSpan.FromSeconds(2) &&
        telemetry.HeadingDeg.HasValue && telemetry.SpeedKt.HasValue;

    private void SetAvailabilityState(ModuleCommandContext context, ParticipantState state, string availability)
    {
        state.Availability = availability;
        context.SetOperationalState("tankerAvailability", availability);
    }

    private bool HasNonTerminalOperation(string participantId) => _operations.Values.Any(operation => operation.TankerId == participantId && !IsTerminal(operation.State));
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

    private void EnsureBoomCompatible(string? receiverType, string? tankerType)
    {
        var snapshot = registry.Current ?? throw new InvalidOperationException("AAR registry is unavailable.");
        var receiver = ResolveProfile(snapshot, receiverType);
        var tanker = ResolveProfile(snapshot, tankerType);
        if (receiver is null || tanker is null || !receiver.CanReceive || !tanker.CanTanker ||
            !receiver.ReceiverSystems.Any(system => system.Method == "BoomReceptacle") ||
            !tanker.TankerSystems.Any(system => system.Method == "Boom"))
            throw new InvalidOperationException("The registered profiles do not share the v0.16 boom method.");
    }

    private static AarAircraftProfile? ResolveProfile(AarRegistrySnapshot snapshot, string? aircraftType)
    {
        var designator = new string((aircraftType ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return snapshot.Profiles.FirstOrDefault(profile => profile.Enabled && profile.IcaoDesignators.Contains(designator, StringComparer.Ordinal));
    }

    public const double DefaultBoomLimitKgPerSecond = 10;
    public static double EffectiveBoomFlow(AarAircraftProfile tanker, AarAircraftProfile receiver)
    {
        var tankerLimit = tanker.TankerSystems.Where(system => system.Method == "Boom").Select(system => system.MaxOffloadKgPerSecond).FirstOrDefault(value => value.HasValue) ?? DefaultBoomLimitKgPerSecond;
        var receiverLimit = receiver.ReceiverSystems.Where(system => system.Method == "BoomReceptacle").Select(system => system.MaxReceiveKgPerSecond).FirstOrDefault(value => value.HasValue) ?? DefaultBoomLimitKgPerSecond;
        return Math.Min(tankerLimit, receiverLimit);
    }

    private object OperationView(AarOperation operation) => new
    {
        operationId = operation.Id,
        requestId = operation.RequestId,
        tankerParticipantId = operation.TankerId,
        receiverParticipantId = operation.ReceiverId,
        slot = operation.Slot,
        state = operation.State,
        operationRevision = operation.Revision,
        plannedKg = operation.PlannedKg,
        transferredKg = operation.TransferredKg,
        remainingKg = Math.Max(0, operation.PlannedKg - operation.TransferredKg),
        effectiveFlowKgPerSecond = operation.EffectiveFlowKgPerSecond,
        registryVersion = operation.RegistryVersion,
        clearanceValid = operation.ClearanceValid,
        acceptedAt = operation.AcceptedAt
    };

    private static object RequestView(AarRequest request) => new
    {
        requestId = request.Id,
        receiverParticipantId = request.ReceiverId,
        tankerParticipantId = request.TankerId,
        requestedKg = request.RequestedKg,
        full = request.RequestedKg is null,
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
        slot = operation.Slot
    };

    private static string ReceiverStatus(string state) => state switch
    {
        "Accepted" => "REQUEST_ACCEPTED",
        "PreContact" => "PROCEED_TO_PRECONTACT",
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

    private void Notify(ModuleCommandContext context, string participantId, string kind, string? operationId, long? revision, object payload) =>
        context.SendEvent(participantId, kind, operationId, revision, payload);

    private static CommandResponse Result(string kind, string? operationId, long? revision, object payload, string? requestId = null) =>
        new(kind, operationId, revision, payload, requestId);

    private static string String(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{name} is required.");
        return value.GetString()!;
    }

    private static double Number(JsonElement payload, string name, double min, double max)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result) || result < min || result > max)
            throw new ArgumentException($"{name} must be a finite number from {min} to {max}.");
        return result;
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
        cached = null!;
        return false;
    }

    private void Remember(string participantId, string messageId, string requestHash, CommandResponse response)
    {
        if (!_results.TryGetValue(participantId, out var entries)) _results[participantId] = entries = new(StringComparer.Ordinal);
        entries[messageId] = new CachedResult(requestHash, response.Kind, response.OperationId, response.OperationRevision, response.Payload,
            response.RequestId, _clock.GetUtcNow());
    }

    private void ExpireResults()
    {
        var now = _clock.GetUtcNow();
        foreach (var participant in _results.Values)
            foreach (var pair in participant.Where(pair => GetExpiry(pair.Value) <= now).ToArray()) participant.Remove(pair.Key);
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
        public Queue<AarPose> Poses { get; } = new();
    }

    private sealed class AarRequest(string id, string receiverId, string tankerId, double? requestedKg, DateTimeOffset requestedAt)
    {
        public string Id { get; } = id;
        public string ReceiverId { get; } = receiverId;
        public string TankerId { get; } = tankerId;
        public double? RequestedKg { get; } = requestedKg;
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public string Status { get; set; } = "Pending";
        public string? OperationId { get; set; }
        public DateTimeOffset? TerminalAt { get; set; }
    }

    private sealed class AarOperation(string id, string requestId, string tankerId, string receiverId,
        string? tankerClientInstanceId, string? receiverClientInstanceId, long tankerGeneration, long receiverGeneration,
        string slot, string state, long revision, double plannedKg, double transferredKg, double effectiveFlowKgPerSecond, int registryVersion,
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
        public double PlannedKg { get; } = plannedKg;
        public double TransferredKg { get; set; } = transferredKg;
        public double EffectiveFlowKgPerSecond { get; } = effectiveFlowKgPerSecond;
        public int RegistryVersion { get; } = registryVersion;
        public AarAircraftProfile TankerProfile { get; } = tankerProfile;
        public AarAircraftProfile ReceiverProfile { get; } = receiverProfile;
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public bool ClearanceValid { get; set; }
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
    }

    private sealed class AarTransferProposal(string id, double targetCumulativeKg, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public double TargetCumulativeKg { get; } = targetCumulativeKg;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public double? TankerAppliedKg { get; set; }
        public double? ReceiverAppliedKg { get; set; }
    }

    private sealed record CommandResponse(string Kind, string? OperationId, long? OperationRevision, object Payload, string? RequestId = null);
    private sealed record CachedResult(string RequestHash, string Kind, string? OperationId, long? OperationRevision, object Payload,
        string? RequestId, DateTimeOffset CreatedAt);
}
