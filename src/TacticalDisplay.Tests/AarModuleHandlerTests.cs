using System.Text.Json;
using TacticalLink.Server.Aar;
using TacticalLink.Server.Modules;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarModuleHandlerTests
{
    [Fact]
    public async Task RequestRetryAfterNewConnectionGenerationReturnsSameRequest()
    {
        var rig = new Rig();
        await rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true });
        await rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });
        await rig.Send("tanker", "JOIN_AS_TANKER", new { });
        await rig.Send("tanker", "SET_PROTECTED_RESERVE", new { protectedReserveKg = 200 });
        await rig.Send("tanker", "SET_TANKER_AVAILABILITY", new { availability = "Available" });

        var first = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = false, requestedKg = 100 }, "request-once", 1);
        var replay = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = false, requestedKg = 100 }, "request-once", 4);

        Assert.Equal("REQUEST_QUEUED", first.LastKind);
        Assert.Contains(replay.Events, item => item.Kind == "REQUEST_QUEUED");
        Assert.Equal(1, rig.RequestCount);
        Assert.Equal(rig.RequestIdFrom(first), rig.RequestIdFrom(replay));
        Assert.Contains(replay.Events, item => item.Kind == "REQUEST_SNAPSHOT");
    }

    [Fact]
    public async Task ReusingMessageIdWithDifferentPayloadIsRejected()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = false, requestedKg = 100 }, "request-conflict");

        var conflict = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = false, requestedKg = 120 }, "request-conflict", 3);

        Assert.Equal("MODULE_ERROR", conflict.LastKind);
        Assert.Contains("IDEMPOTENCY_CONFLICT", JsonSerializer.Serialize(conflict.LastPayload));
        Assert.Equal(1, rig.RequestCount);
    }

    [Fact]
    public async Task QueueKeepsOnlyOneActiveAndOneCommittedNextAndPendingDoesNotReserveFuel()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var firstRequest = await rig.Request("receiver", "tanker", 100);
        var firstId = rig.RequestIdFrom(firstRequest);
        var firstOperation = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = firstId });
        Assert.Equal("Accepted", rig.OperationViewFrom(firstOperation).GetProperty("state").GetString());
        Assert.Equal(100, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());

        rig.AddReceiver("receiver-2");
        await rig.Send("receiver-2", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });
        var nextRequest = await rig.Request("receiver-2", "tanker", 150);
        var nextId = rig.RequestIdFrom(nextRequest);
        var nextOperation = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = nextId });
        Assert.Equal("CommittedNext", rig.OperationViewFrom(nextOperation).GetProperty("slot").GetString());
        Assert.Equal(250, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());

        rig.AddReceiver("receiver-3");
        await rig.Send("receiver-3", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });
        var pending = await rig.Request("receiver-3", "tanker", 50);
        var thirdAcceptance = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(pending) });
        Assert.Equal("MODULE_ERROR", thirdAcceptance.LastKind);
        Assert.Equal(250, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());
        Assert.Equal("Pending", rig.RequestStatusFrom(pending));
        Assert.Equal(550, rig.FuelSummary("tanker").GetProperty("availableToPromiseKg").GetDouble());
    }

    [Fact]
    public async Task RejectAndReceiverCancelPublishFreshTankerQueueSnapshots()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();

        var rejectedRequest = await rig.Request("receiver", "tanker", 100);
        var rejected = await rig.Send("tanker", "REJECT_REQUEST", new { requestId = rig.RequestIdFrom(rejectedRequest) });
        var rejectedQueue = Assert.Single(rejected.Events, item => item.Kind == "QUEUE_UPDATED");
        Assert.Empty(rejectedQueue.Payload.GetProperty("pending").EnumerateArray());

        var cancelledRequest = await rig.Request("receiver", "tanker", 100);
        var cancelled = await rig.Send("receiver", "CANCEL_REQUEST", new { requestId = rig.RequestIdFrom(cancelledRequest) });
        var cancelledQueue = Assert.Single(cancelled.Events, item => item.Kind == "QUEUE_UPDATED");
        Assert.Empty(cancelledQueue.Payload.GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task PromotedCommittedReceiverMustFollowAsternThenTankerClearsContact()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        rig.AddReceiver("receiver-2");
        await rig.Send("receiver-2", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });

        var firstRequest = await rig.Request("receiver", "tanker", 100);
        var first = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(firstRequest) });
        var nextRequest = await rig.Request("receiver-2", "tanker", 100);
        var next = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(nextRequest) });
        var firstId = first.LastPayload.GetProperty("operationId").GetString()!;
        var nextId = next.LastPayload.GetProperty("operationId").GetString()!;

        await rig.Send("tanker", "DISCONNECT", new { }, operationId: firstId);
        Assert.Equal("Accepted", rig.OperationState(nextId));

        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: nextId);
        var clearContact = await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: nextId);

        Assert.Equal("ClearedContact", clearContact.LastPayload.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReceiverCannotJoinMultiplePendingQueues()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var first = await rig.Request("receiver", "tanker", 100);
        rig.AddTanker("tanker-2");
        await rig.Send("tanker-2", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true });
        await rig.Send("tanker-2", "JOIN_AS_TANKER", new { });
        await rig.Send("tanker-2", "SET_PROTECTED_RESERVE", new { protectedReserveKg = 200 });
        await rig.Send("tanker-2", "SET_TANKER_AVAILABILITY", new { availability = "Available" });

        var duplicate = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker-2", full = false, requestedKg = 100 });

        Assert.Equal("REQUEST_QUEUED", first.LastKind);
        Assert.Equal("MODULE_ERROR", duplicate.LastKind);
        Assert.Equal(1, rig.RequestCount);
    }

    [Fact]
    public async Task PendingReceiverRequestIsNotifiedAndRestoredByGetState()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();

        var request = await rig.Request("receiver", "tanker", 100);
        var pendingEvent = Assert.Single(request.Events, item => item.Recipient == "receiver" && item.Kind == "REQUEST_PENDING");
        var restored = await rig.Send("receiver", "GET_STATE", new { });

        Assert.Equal(rig.RequestIdFrom(request), pendingEvent.Payload.GetProperty("requestId").GetString());
        Assert.Equal(rig.RequestIdFrom(request), restored.LastPayload.GetProperty("ownPendingRequestId").GetString());
    }

    [Fact]
    public async Task TankerSelectedPlannedKgIsTheInitialCommitmentAndSafeMaximumIsReturned()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var requestId = rig.RequestIdFrom(request);

        var overMaximum = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId, plannedKg = 101 });
        Assert.Equal("MODULE_ERROR", overMaximum.LastKind);
        Assert.Equal("PLANNED_AMOUNT_EXCEEDS_SAFE_MAXIMUM", overMaximum.LastPayload.GetProperty("code").GetString());
        Assert.Equal(100, overMaximum.LastPayload.GetProperty("maxAllowedKg").GetDouble());
        Assert.Equal("Pending", rig.RequestStatusFrom(request));

        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId, plannedKg = 60 });
        Assert.Equal(100, accepted.LastPayload.GetProperty("requestedKg").GetDouble());
        Assert.Equal(60, accepted.LastPayload.GetProperty("plannedKg").GetDouble());
        Assert.Equal(60, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());
    }

    [Fact]
    public async Task TankerAddedQueueEntryHasNoReceiverRequestIntent()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();

        var added = await rig.Send("tanker", "ADD_RECEIVER_TO_QUEUE", new { receiverParticipantId = "receiver" });

        Assert.Equal("TankerAdded", added.LastPayload.GetProperty("source").GetString());
        Assert.Equal("None", added.LastPayload.GetProperty("requestMode").GetString());
        Assert.False(added.LastPayload.GetProperty("full").GetBoolean());
        Assert.True(added.LastPayload.GetProperty("requestedKg").ValueKind == JsonValueKind.Null);
        var receiverNotice = Assert.Single(added.Events, item => item.Recipient == "receiver" && item.Kind == "TANKER_ADDED_RECEIVER");
        Assert.Equal("None", receiverNotice.Payload.GetProperty("requestMode").GetString());
        Assert.Equal("TankerAdded", receiverNotice.Payload.GetProperty("source").GetString());
        Assert.Equal("receiver", receiverNotice.Payload.GetProperty("receiverParticipantId").GetString());
    }

    [Fact]
    public async Task FullRequestIsClampedToReceiverCapacityAtAcceptance()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = true });
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });

        Assert.Equal(400, accepted.LastPayload.GetProperty("plannedKg").GetDouble());
    }

    [Fact]
    public async Task UnreadyTankerCanBecomeAvailableForDryHookup()
    {
        var rig = new Rig();
        await rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = false });
        await rig.Send("tanker", "JOIN_AS_TANKER", new { });
        await rig.Send("tanker", "SET_PROTECTED_RESERVE", new { protectedReserveKg = 200 });

        var result = await rig.Send("tanker", "SET_TANKER_AVAILABILITY", new { availability = "Available" });

        Assert.Equal("TANKER_AVAILABILITY_UPDATED", result.LastKind);
    }

    [Fact]
    public async Task TankerCanAddCompatibleNearbyReceiverWithoutReceiverRequest()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();

        var added = await rig.Send("tanker", "ADD_RECEIVER_TO_QUEUE", new { receiverParticipantId = "receiver" });
        var state = await rig.Send("tanker", "GET_STATE", new { });
        var pending = state.LastPayload.GetProperty("queue").GetProperty("pending")[0];

        Assert.Equal("RECEIVER_ADDED_TO_QUEUE", added.LastKind);
        Assert.Equal("TankerAdded", pending.GetProperty("source").GetString());
        Assert.False(pending.GetProperty("full").GetBoolean());
        Assert.Equal("None", pending.GetProperty("requestMode").GetString());
        Assert.Contains(added.Events, item => item.Recipient == "receiver" && item.Kind == "TANKER_ADDED_RECEIVER");
    }

    [Fact]
    public async Task TankerMayChangePlanWithoutChangingOriginalReceiverRequest()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 150);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;

        var changed = await rig.Send("tanker", "SET_PLANNED_ONLOAD", new { plannedKg = 250 }, operationId: operationId);

        Assert.Equal(250, changed.LastPayload.GetProperty("plannedKg").GetDouble());
        Assert.Equal(150, changed.LastPayload.GetProperty("requestedKg").GetDouble());
    }

    [Fact]
    public async Task TankerMayChoosePlanAsPartOfAcceptanceBeforeCommitment()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 150);

        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request), plannedKg = 75 });

        Assert.Equal(75, accepted.LastPayload.GetProperty("plannedKg").GetDouble());
        Assert.Equal(75, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());
    }

    [Fact]
    public async Task TankerCanDisconnectAnAsternReceiverNormally()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);

        var disconnected = await rig.Send("tanker", "DISCONNECT", new { }, operationId: operationId);

        Assert.Equal("OPERATION_COMPLETE", disconnected.LastKind);
        Assert.Equal("Complete", rig.OperationState(operationId));
    }

    [Theory]
    [InlineData("DISCONNECT", "Suspended", "OPERATION_SUSPENDED")]
    [InlineData("STOP_TRANSFER", "Contact", "TRANSFER_STOPPED")]
    [InlineData("HOLD", "Astern", "HOLD")]
    [InlineData("BREAKAWAY", "Breakaway", "BREAKAWAY")]
    public async Task SafetyCommandDominatesPendingProposalAndSettlesOnlyExactLateAcknowledgement(string safetyCommand, string expectedState, string expectedKind)
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);

        var now = DateTimeOffset.UtcNow;
        var tankerPose = new { timestampUtc = now, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var receiverPose = new { timestampUtc = now.AddMilliseconds(5), latitudeDeg = 60d - (30d / 111000d), longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose);
        await rig.Send("receiver", "POSE_UPDATE", receiverPose);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddMilliseconds(10) });
        await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);

        var proposal = new TaskCompletionSource<AarServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proposalCount = 0;
        rig.Handler.EventReady += message =>
        {
            if (message.Kind == "TRANSFER_PROPOSAL" && message.ParticipantId == "tanker")
            {
                Interlocked.Increment(ref proposalCount);
                proposal.TrySetResult(message);
            }
        };
        await rig.Handler.StartAsync(CancellationToken.None);
        try
        {
            var pending = await proposal.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var proposalPayload = JsonSerializer.SerializeToElement(pending.Payload);
            var proposalId = proposalPayload.GetProperty("proposalId").GetString()!;
            var targetKg = proposalPayload.GetProperty("targetCumulativeKg").GetDouble();
            var deltaKg = proposalPayload.GetProperty("deltaKg").GetDouble();
            SendResult safety;
            var commandPeer = safetyCommand == "DISCONNECT" ? "receiver" : "tanker";
            safety = await rig.Send(commandPeer, safetyCommand, new { }, operationId: operationId);
            Assert.Equal(expectedKind, safety.LastKind);
            Assert.Equal(expectedState, rig.OperationState(operationId));
            var operation = rig.OperationForTests(operationId);
            Assert.False(operation!.Value.FuelOnAuthorized);
            var unrelatedAck = await rig.Send("tanker", "TRANSFER_ACK", new
            {
                proposalId = "unknown-proposal",
                operationRevision = pending.OperationRevision,
                targetCumulativeKg = targetKg,
                appliedCumulativeKg = targetKg,
                appliedKg = deltaKg
            }, operationId: operationId);
            Assert.Equal("MODULE_ERROR", unrelatedAck.LastKind);

            var wrongTargetAck = await rig.Send("tanker", "TRANSFER_ACK", new
            {
                proposalId,
                operationRevision = pending.OperationRevision,
                targetCumulativeKg = targetKg + 1,
                appliedCumulativeKg = targetKg,
                appliedKg = deltaKg
            }, operationId: operationId);
            Assert.Equal("MODULE_ERROR", wrongTargetAck.LastKind);

            var tankerAck = await rig.Send("tanker", "TRANSFER_ACK", new
            {
                proposalId,
                operationRevision = pending.OperationRevision,
                targetCumulativeKg = targetKg,
                appliedCumulativeKg = targetKg,
                appliedKg = deltaKg
            }, operationId: operationId);
            Assert.Equal("TRANSFER_SETTLEMENT_RECORDED", tankerAck.LastKind);
            var receiverAck = await rig.Send("receiver", "TRANSFER_ACK", new
            {
                proposalId,
                operationRevision = pending.OperationRevision,
                targetCumulativeKg = targetKg,
                appliedCumulativeKg = targetKg,
                appliedKg = deltaKg
            }, operationId: operationId);
            Assert.Equal("TRANSFER_SETTLEMENT_ACCEPTED", receiverAck.LastKind);
            Assert.Equal(expectedState, rig.OperationState(operationId));
            var settledOperation = rig.OperationForTests(operationId);
            Assert.Equal(targetKg, settledOperation!.Value.TransferredKg, 3);
            await Task.Delay(350);
            Assert.Equal(1, Volatile.Read(ref proposalCount));
        }
        finally
        {
            await rig.Handler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TankerControlsAsternAndContactClearanceAndReceiverCannotIssueClearance()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;

        var receiverClear = await rig.Send("receiver", "CLEAR_ASTERN", new { }, operationId: operationId);
        var bypass = await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);
        var clearedAstern = await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        var clearedContact = await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);
        var receiverStart = await rig.Send("receiver", "START_TRANSFER", new { }, operationId: operationId);

        Assert.Equal("MODULE_ERROR", receiverClear.LastKind);
        Assert.Equal("CLEARED_ASTERN", clearedAstern.LastKind);
        Assert.Equal("MODULE_ERROR", bypass.LastKind);
        Assert.Equal("CLEARED_CONTACT", clearedContact.LastKind);
        Assert.Equal("ClearedContact", clearedContact.LastPayload.GetProperty("state").GetString());
        Assert.Equal("MODULE_ERROR", receiverStart.LastKind);
        Assert.Equal(0, clearedContact.LastPayload.GetProperty("transferredKg").GetDouble());
    }

    [Fact]
    public async Task DryHookupRejectsFuelStartAndLeavesReceiverViewPrivate()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;

        var mode = await rig.Send("tanker", "SET_TRANSFER_MODE", new { transferMode = "DryHookup" }, operationId: operationId);
        var astern = await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        var clearance = await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);
        var start = await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);
        var receiverState = await rig.Send("receiver", "GET_STATE", new { });
        var serialized = JsonSerializer.Serialize(receiverState.LastPayload);

        Assert.Equal("DryHookup", mode.LastPayload.GetProperty("transferMode").GetString());
        Assert.Equal("CLEARED_ASTERN", astern.LastKind);
        Assert.Equal("CLEARED_CONTACT", clearance.LastKind);
        Assert.Equal("MODULE_ERROR", start.LastKind);
        Assert.DoesNotContain("plannedKg", serialized);
        Assert.DoesNotContain("transferredKg", serialized);
    }

    [Fact]
    public async Task DryHookupCanBeAcceptedWithoutFuelAdaptersOrFuelCommitment()
    {
        var rig = new Rig();
        await rig.Send("tanker", "JOIN_AS_TANKER", new { });
        await rig.Send("tanker", "SET_TANKER_AVAILABILITY", new { availability = "Available" });
        var request = await rig.Send("receiver", "REQUEST_REFUEL", new { tankerParticipantId = "tanker", full = true, transferMode = "DryHookup" });
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;

        Assert.Equal("DryHookup", accepted.LastPayload.GetProperty("transferMode").GetString());
        Assert.Equal(0, accepted.LastPayload.GetProperty("plannedKg").GetDouble());
        Assert.Equal(0, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());
        await rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = false });
        await rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = false });
        Assert.Equal("Accepted", rig.OperationState(operationId));
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);
        var start = await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);
        Assert.Equal("MODULE_ERROR", start.LastKind);
        Assert.Equal("ClearedContact", rig.OperationState(operationId));
    }

    [Fact]
    public async Task ContactDoesNotAuthorizeFuelUntilTankerStartsTransfer()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);

        var now = DateTimeOffset.UtcNow;
        var tankerPose = new { timestampUtc = now, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var receiverPose = new { timestampUtc = now.AddMilliseconds(5), latitudeDeg = 60d - (30d / 111000d), longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose);
        var contact = await rig.Send("receiver", "POSE_UPDATE", receiverPose);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddMilliseconds(10) });

        Assert.Equal("Contact", rig.OperationState(operationId));
        Assert.DoesNotContain(contact.Events, item => item.Kind == "TRANSFER_PROPOSAL");
        var beforeFuelOn = await rig.Send("tanker", "GET_STATE", new { });
        Assert.Equal(0, beforeFuelOn.LastPayload.GetProperty("operations")[0].GetProperty("transferredKg").GetDouble());

        var started = await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);

        Assert.Equal("REFUELING_STARTED", started.LastKind);
        Assert.Equal("Refueling", rig.OperationState(operationId));
    }

    [Fact]
    public async Task ContactLossStopsTransferAndKeepsOperationLiveInAstern()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);

        var now = DateTimeOffset.UtcNow;
        var tankerPose = new { timestampUtc = now, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var receiverPose = new { timestampUtc = now.AddMilliseconds(5), latitudeDeg = 60d - (30d / 111000d), longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose);
        await rig.Send("receiver", "POSE_UPDATE", receiverPose);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddMilliseconds(10) });
        await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);

        var lost = new { timestampUtc = now.AddSeconds(1), latitudeDeg = 60d - (500d / 111000d), longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddSeconds(1) });
        await rig.Send("receiver", "POSE_UPDATE", lost);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddSeconds(2) });
        await rig.Send("receiver", "POSE_UPDATE", lost with { timestampUtc = now.AddSeconds(2) });

        Assert.Equal("Astern", rig.OperationState(operationId));
        var state = await rig.Send("tanker", "GET_STATE", new { });
        var operation = state.LastPayload.GetProperty("operations").EnumerateArray().Single(item => item.GetProperty("operationId").GetString() == operationId);
        Assert.False(operation.GetProperty("fuelOnAuthorized").GetBoolean());
        Assert.Equal("Active", operation.GetProperty("slot").GetString());
    }

    [Fact]
    public async Task ContactLossBeforeTransferRevokesClearanceAndBlocksStart()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);

        var start = DateTimeOffset.UtcNow.AddMilliseconds(-500);
        var tankerPose = new { timestampUtc = start, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var receiverPose = new { timestampUtc = start.AddMilliseconds(5), latitudeDeg = 60d - (30d / 111000d), longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose);
        await rig.Send("receiver", "POSE_UPDATE", receiverPose);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = start.AddMilliseconds(10) });
        Assert.Equal("Contact", rig.OperationState(operationId));

        var lost = receiverPose with { latitudeDeg = 60d - (500d / 111000d) };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = start.AddMilliseconds(305) });
        await rig.Send("receiver", "POSE_UPDATE", lost with { timestampUtc = start.AddMilliseconds(305) });
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = start.AddMilliseconds(605) });
        await rig.Send("receiver", "POSE_UPDATE", lost with { timestampUtc = start.AddMilliseconds(605) });

        Assert.Equal("Astern", rig.OperationState(operationId));
        var startTransfer = await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId);
        Assert.Equal("MODULE_ERROR", startTransfer.LastKind);
    }

    [Fact]
    public async Task ReceiverStateContainsNoFuelAmountsRatesOrProgress()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var state = await rig.Send("receiver", "GET_STATE", new { });
        var serialized = JsonSerializer.Serialize(state.LastPayload);

        Assert.DoesNotContain("plannedKg", serialized);
        Assert.DoesNotContain("transferredKg", serialized);
        Assert.DoesNotContain("remainingKg", serialized);
        Assert.DoesNotContain("effectiveFlow", serialized);
        Assert.DoesNotContain("currentFuelKg", serialized);
    }

    [Fact]
    public async Task NetworkReconnectSuspendsAndRequiresFreshClearAstern()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);

        rig.Handler.OnParticipantDisconnected("user-r", "receiver", explicitDisconnect: false);
        Assert.Equal("Suspended", rig.OperationState(operationId));

        rig.Reconnect("receiver", 2, "instance-r");
        await rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true, appliedOperationId = operationId, lastAppliedTransferredKg = 0 });
        await rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true, appliedOperationId = operationId, lastAppliedTransferredKg = 0 });
        var reconciled = await rig.Send("receiver", "RECONCILE", new { operationId }, operationId: operationId);

        Assert.Equal("Accepted", reconciled.LastPayload.GetProperty("state").GetString());
        Assert.False(reconciled.LastPayload.TryGetProperty("clearanceValid", out _));
        Assert.False(reconciled.LastPayload.TryGetProperty("plannedKg", out _));
        Assert.False(reconciled.LastPayload.TryGetProperty("transferredKg", out _));
        Assert.Equal(4, reconciled.LastPayload.GetProperty("operationRevision").GetInt64());
    }

    [Fact]
    public async Task DesktopProcessRestartFailsActiveOperation()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;

        rig.Reconnect("receiver", 2, "new-process-instance");

        Assert.Equal("Failed", rig.OperationState(operationId));
    }

    [Theory]
    [InlineData(60d, null, 10d)]
    [InlineData(null, 15d, 10d)]
    [InlineData(null, null, 10d)]
    [InlineData(20d, 15d, 15d)]
    [InlineData(8d, 25d, 8d)]
    public void ResolveV016AarFlowUsesIndependentFallbacksAndMinimum(double? tankerLimit, double? receiverLimit, double expected)
    {
        var tanker = new AarAircraftProfile("tanker", "Tanker", ["TSTK"], true, true, false,
            [new("WingDrogue", tankerLimit, tankerLimit.HasValue ? "confirmed_aircraft_specific_value" : "unknown", [])], [], [], null, "");
        var receiver = new AarAircraftProfile("receiver", "Receiver", ["TSTR"], true, false, true,
            [], [new("Probe", receiverLimit, receiverLimit.HasValue ? "confirmed_aircraft_specific_value" : "unknown", [])], [], null, "");

        Assert.Equal(expected, AarModuleHandler.ResolveV016AarFlow(tanker, receiver));
    }

    [Fact]
    public void ResolveV016AarFlowUsesConservativeLimitAcrossMultipleSystems()
    {
        var tanker = Rig.TankerProfile with
        {
            TankerSystems = [new("Boom", 30, "confirmed_aircraft_specific_value", []), new("WingDrogue", 20, "confirmed_aircraft_specific_value", [])]
        };
        var receiver = Rig.ReceiverProfile with
        {
            ReceiverSystems = [new("BoomReceptacle", 40, "confirmed_aircraft_specific_value", []), new("Probe", 25, "confirmed_aircraft_specific_value", [])]
        };

        Assert.Equal(20, AarModuleHandler.ResolveV016AarFlow(tanker, receiver));
    }

    [Theory]
    [InlineData("Boom", "BoomReceptacle")]
    [InlineData("Boom", "Probe")]
    [InlineData("CenterlineDrogue", "BoomReceptacle")]
    [InlineData("WingDrogue", "Probe")]
    public async Task RequestRefuelDoesNotRequireMatchingPhysicalMethods(string tankerMethod, string receiverMethod)
    {
        var tanker = Rig.TankerProfile with { TankerSystems = [new(tankerMethod, null, "unknown", [])] };
        var receiver = Rig.ReceiverProfile with { ReceiverSystems = [new(receiverMethod, null, "unknown", [])] };
        var rig = new Rig(tanker, receiver);
        await rig.PrepareTankerAndReceiver();

        var result = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(result) });

        Assert.Equal("REQUEST_QUEUED", result.LastKind);
        Assert.Equal("REQUEST_ACCEPTED", accepted.LastKind);
    }

    [Fact]
    public async Task F18ProbeReceiverCanBeAcceptedByBoomTankerInV016()
    {
        var tanker = Rig.TankerProfile with { TankerSystems = [new("Boom", null, "unknown", [])] };
        var receiver = Rig.ReceiverProfile with { ProfileKey = "f18", DisplayName = "F-18", IcaoDesignators = ["F18"], ReceiverSystems = [new("Probe", null, "unknown", [])] };
        var rig = new Rig(tanker, receiver, receiverType: "F18");
        await rig.PrepareTankerAndReceiver();

        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });

        Assert.Equal("REQUEST_QUEUED", request.LastKind);
        Assert.Equal("REQUEST_ACCEPTED", accepted.LastKind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-tanker")]
    [InlineData("disabled")]
    [InlineData("disabled-tanker")]
    [InlineData("receiver-role")]
    [InlineData("tanker-role")]
    public async Task RequestRefuelRequiresEnabledProfilesAndRoleCapabilities(string invalidProfile)
    {
        var tanker = Rig.TankerProfile;
        var receiver = Rig.ReceiverProfile;
        var receiverType = "F16";
        var tankerType = "K35R";
        if (invalidProfile == "missing") receiverType = "XXXX";
        if (invalidProfile == "missing-tanker") tankerType = "XXXX";
        if (invalidProfile == "disabled") receiver = receiver with { Enabled = false };
        if (invalidProfile == "disabled-tanker") tanker = tanker with { Enabled = false };
        if (invalidProfile == "receiver-role") receiver = receiver with { CanReceive = false };
        if (invalidProfile == "tanker-role") tanker = tanker with { CanTanker = false };
        var rig = new Rig(tanker, receiver, receiverType, tankerType);
        await rig.PrepareTankerAndReceiver();

        var result = await rig.Request("receiver", "tanker", 100);

        Assert.Equal("MODULE_ERROR", result.LastKind);
    }

    [Theory]
    [InlineData("STOP_TRANSFER", "Contact")]
    [InlineData("HOLD", "Astern")]
    [InlineData("CONTACT_LOSS", "Astern")]
    public async Task IncompleteCancelledSettlementTimesOutToSuspendedAndRequiresReconcile(string safetyCommand, string safeState)
    {
        var fixture = await CreatePendingTransferAsync();
        if (safetyCommand == "CONTACT_LOSS")
        {
            var now = fixture.Clock.GetUtcNow().AddMilliseconds(30);
            var loss = await fixture.Rig.Send("receiver", "POSE_UPDATE", new
            {
                timestampUtc = now,
                latitudeDeg = 60d - 30d / 111000d,
                longitudeDeg = 25d + 200d / (111000d * Math.Cos(60d * Math.PI / 180)),
                altitudeMeters = 9990d,
                headingDeg = 0d,
                velocityNorthMps = 100d,
                velocityEastMps = 0d,
                velocityDownMps = 0d
            });
            Assert.Null(loss.LastKind);
            Assert.Equal("Astern", fixture.Rig.OperationState(fixture.OperationId));
        }
        else
        {
            var stopped = await fixture.Rig.Send("tanker", safetyCommand, new { }, operationId: fixture.OperationId);
            Assert.NotEqual("MODULE_ERROR", stopped.LastKind);
        }
        Assert.Equal(safeState, fixture.Rig.OperationState(fixture.OperationId));

        var tankerAck = await AcknowledgeCancelledTransferAsync(fixture, "tanker");
        Assert.Equal("TRANSFER_SETTLEMENT_RECORDED", tankerAck.LastKind);
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        fixture.Rig.Handler.ProcessLifecycleTick();

        Assert.Equal("Suspended", fixture.Rig.OperationState(fixture.OperationId));
        Assert.False(fixture.Rig.OperationForTests(fixture.OperationId)!.Value.FuelOnAuthorized);
        var suspended = fixture.Rig.HandlerEvents.First(item => item.Kind == "OPERATION_SUSPENDED" && item.OperationId == fixture.OperationId &&
            JsonSerializer.SerializeToElement(item.Payload).GetProperty("reason").GetString() == "TRANSFER_SETTLEMENT_TIMEOUT");
        Assert.Equal("TRANSFER_SETTLEMENT_TIMEOUT", JsonSerializer.SerializeToElement(suspended.Payload).GetProperty("reason").GetString());
        Assert.Equal("MODULE_ERROR", (await fixture.Rig.Send("tanker", "START_TRANSFER", new { }, operationId: fixture.OperationId)).LastKind);

        fixture.Rig.RefreshPeerTelemetries();
        await fixture.Rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true, appliedOperationId = fixture.OperationId, lastAppliedTransferredKg = fixture.TargetKg });
        await fixture.Rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true, appliedOperationId = fixture.OperationId, lastAppliedTransferredKg = fixture.TargetKg });
        var reconciled = await fixture.Rig.Send("receiver", "RECONCILE", new { operationId = fixture.OperationId }, operationId: fixture.OperationId);
        Assert.Equal("OPERATION_RECONCILED", reconciled.LastKind);
        Assert.Equal("Accepted", fixture.Rig.OperationState(fixture.OperationId));
    }

    [Fact]
    public async Task ExactCancelledTransferSettlementBeforeDeadlineIsNotSuspendedLater()
    {
        var fixture = await CreatePendingTransferAsync();
        await fixture.Rig.Send("tanker", "STOP_TRANSFER", new { }, operationId: fixture.OperationId);
        Assert.Equal("TRANSFER_SETTLEMENT_RECORDED", (await AcknowledgeCancelledTransferAsync(fixture, "tanker")).LastKind);
        Assert.Equal("TRANSFER_SETTLEMENT_ACCEPTED", (await AcknowledgeCancelledTransferAsync(fixture, "receiver")).LastKind);
        Assert.Equal(fixture.TargetKg, fixture.Rig.OperationForTests(fixture.OperationId)!.Value.TransferredKg, 3);

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        fixture.Rig.RefreshPeerTelemetriesAndClearPoses();
        await fixture.Rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true });
        await fixture.Rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });
        var refreshedNow = fixture.Clock.GetUtcNow();
        var freshTankerPose = new { timestampUtc = refreshedNow, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var freshReceiverPose = new { timestampUtc = refreshedNow, latitudeDeg = 60d - 30d / 111000d, longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await fixture.Rig.Send("tanker", "POSE_UPDATE", freshTankerPose);
        await fixture.Rig.Send("receiver", "POSE_UPDATE", freshReceiverPose);
        fixture.Rig.Handler.ProcessLifecycleTick();

        Assert.Equal("Contact", fixture.Rig.OperationState(fixture.OperationId));
        Assert.DoesNotContain(fixture.Rig.HandlerEvents, item => item.Kind == "OPERATION_SUSPENDED" &&
            JsonSerializer.SerializeToElement(item.Payload).TryGetProperty("reason", out var reason) && reason.GetString() == "TRANSFER_SETTLEMENT_TIMEOUT");
    }

    [Fact]
    public async Task ExpiredBreakawaySettlementNeverResurrectsTerminalOperation()
    {
        var fixture = await CreatePendingTransferAsync();
        Assert.Equal("BREAKAWAY", (await fixture.Rig.Send("tanker", "BREAKAWAY", new { }, operationId: fixture.OperationId)).LastKind);
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        fixture.Rig.Handler.ProcessLifecycleTick();

        Assert.Equal("Breakaway", fixture.Rig.OperationState(fixture.OperationId));
        Assert.False(fixture.Rig.OperationForTests(fixture.OperationId)!.Value.FuelOnAuthorized);
        Assert.DoesNotContain(fixture.Rig.HandlerEvents, item => item.Kind == "OPERATION_SUSPENDED" && item.OperationId == fixture.OperationId);
    }

    private static async Task<PendingTransferFixture> CreatePendingTransferAsync()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var rig = new Rig(clock);
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "CLEAR_ASTERN", new { }, operationId: operationId);
        await rig.Send("tanker", "CLEAR_CONTACT", new { }, operationId: operationId);

        var now = clock.GetUtcNow();
        var tankerPose = new { timestampUtc = now, latitudeDeg = 60d, longitudeDeg = 25d, altitudeMeters = 10000d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        var receiverPose = new { timestampUtc = now.AddMilliseconds(5), latitudeDeg = 60d - 30d / 111000d, longitudeDeg = 25d, altitudeMeters = 9990d, headingDeg = 0d, velocityNorthMps = 100d, velocityEastMps = 0d, velocityDownMps = 0d };
        await rig.Send("tanker", "POSE_UPDATE", tankerPose);
        await rig.Send("receiver", "POSE_UPDATE", receiverPose);
        await rig.Send("tanker", "POSE_UPDATE", tankerPose with { timestampUtc = now.AddMilliseconds(10) });
        Assert.Equal("CONTACT_CAPTURED", rig.HandlerEvents.Last(item => item.Kind == "CONTACT_CAPTURED").Kind);
        Assert.Equal("REFUELING_STARTED", (await rig.Send("tanker", "START_TRANSFER", new { }, operationId: operationId)).LastKind);

        rig.Handler.ProcessLifecycleTick();
        var proposal = rig.HandlerEvents.Last(item => item.Kind == "TRANSFER_PROPOSAL" && item.ParticipantId == "tanker");
        var payload = JsonSerializer.SerializeToElement(proposal.Payload);
        return new PendingTransferFixture(rig, clock, operationId, proposal, payload.GetProperty("proposalId").GetString()!,
            payload.GetProperty("targetCumulativeKg").GetDouble(), payload.GetProperty("deltaKg").GetDouble());
    }

    private static Task<SendResult> AcknowledgeCancelledTransferAsync(PendingTransferFixture fixture, string participant) =>
        fixture.Rig.Send(participant, "TRANSFER_ACK", new
        {
            proposalId = fixture.ProposalId,
            operationRevision = fixture.Proposal.OperationRevision,
            targetCumulativeKg = fixture.TargetKg,
            appliedCumulativeKg = fixture.TargetKg,
            appliedKg = fixture.DeltaKg
        }, operationId: fixture.OperationId);

    private sealed class Rig
    {
        public static AarAircraftProfile TankerProfile { get; } = new("kc135r", "KC-135R/T", ["K35R"], true, true, false,
            [new("Boom", 45, "confirmed_aircraft_specific_value", ["source-1"])], [], [], null, "");
        public static AarAircraftProfile ReceiverProfile { get; } = new("f16", "F-16", ["F16"], true, false, true,
            [], [new("BoomReceptacle", null, "unknown", [])], [], null, "");
        private readonly AarModuleHandler _handler;
        private readonly ModuleRouter _router;
        private readonly TimeProvider _clock;
        private readonly Dictionary<string, AarPeerSnapshot> _peers;
        private int _nextId;

        public Rig(AarAircraftProfile? tankerProfile = null, AarAircraftProfile? receiverProfile = null, string receiverType = "F16", string tankerType = "K35R")
            : this(null, tankerProfile, receiverProfile, receiverType, tankerType) { }

        public Rig(TimeProvider? clock)
            : this(clock, null, null, "F16", "K35R") { }

        private Rig(TimeProvider? clock, AarAircraftProfile? tankerProfile, AarAircraftProfile? receiverProfile, string receiverType, string tankerType)
        {
            _clock = clock ?? TimeProvider.System;
            _peers = new Dictionary<string, AarPeerSnapshot>(StringComparer.Ordinal)
            {
                ["tanker"] = new("tanker", "user-t", "1001", "TANKER", tankerType, true, "instance-t", 1, new HashSet<string>(["aar.tanker"]), new Dictionary<string, string>(), FreshTelemetry()),
                ["receiver"] = new("receiver", "user-r", "1002", "VIPER11", receiverType, true, "instance-r", 1, new HashSet<string>(["aar.receiver"]), new Dictionary<string, string>(), FreshTelemetry())
            };
            _handler = new AarModuleHandler(new Registry(tankerProfile ?? TankerProfile, receiverProfile ?? ReceiverProfile), timeProvider: _clock,
                contactConfiguration: new AarContactConfiguration(CaptureDebounce: TimeSpan.Zero, ReleaseDebounce: TimeSpan.Zero));
            _handler.EventReady += _events.Add;
            _router = new ModuleRouter([_handler]);
        }

        public int RequestCount => _handler.RequestCountForTests;
        public AarModuleHandler Handler => _handler;
        public IReadOnlyList<AarServerEvent> HandlerEvents => _events;
        private readonly List<AarServerEvent> _events = [];

        public async Task<SendResult> PrepareTankerAndReceiver()
        {
            await Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true });
            await Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true });
            await Send("tanker", "JOIN_AS_TANKER", new { });
            await Send("tanker", "SET_PROTECTED_RESERVE", new { protectedReserveKg = 200 });
            return await Send("tanker", "SET_TANKER_AVAILABILITY", new { availability = "Available" });
        }

        public Task<SendResult> Request(string receiver, string tanker, double kg) =>
            Send(receiver, "REQUEST_REFUEL", new { tankerParticipantId = tanker, full = false, requestedKg = kg });

        public void AddReceiver(string participantId)
        {
            _peers.Add(participantId, new AarPeerSnapshot(participantId, "user-" + participantId, "1003", participantId.ToUpperInvariant(), "F16", true,
                "instance-" + participantId, 1, new HashSet<string>(["aar.receiver"]), new Dictionary<string, string>(), FreshTelemetry()));
        }

        public void AddTanker(string participantId)
        {
            _peers.Add(participantId, new AarPeerSnapshot(participantId, "user-" + participantId, "2003", participantId.ToUpperInvariant(), "K35R", true,
                "instance-" + participantId, 1, new HashSet<string>(["aar.tanker"]), new Dictionary<string, string>(), FreshTelemetry()));
        }

        public async Task<SendResult> Send(string participant, string kind, object payload, string? messageId = null, long generation = 1, string? operationId = null)
        {
            var id = messageId ?? "msg-" + Interlocked.Increment(ref _nextId);
            var serializedPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var payloadDoc = JsonDocument.Parse(serializedPayload);
            using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "MODULE_MESSAGE",
                module = "aar",
                moduleProtocolVersion = 1,
                messageId = id,
                kind,
                operationId,
                payload = payloadDoc.RootElement
            }));
            var current = _peers[participant];
            var sent = new List<SentEvent>();
            var connection = new ModuleConnectionContext(participant, generation, () => true,
                (recipient, _, _, eventKind, operationId, revision, correlation, eventPayload) =>
                {
                    sent.Add(new SentEvent(recipient, eventKind, operationId, revision, correlation, JsonSerializer.SerializeToElement(eventPayload)));
                    return true;
                },
                (_, _) => { },
                current,
                () => _peers.Values.ToArray());
            await _router.RouteAsync(envelope.RootElement, connection, CancellationToken.None);
            return new SendResult(sent, sent.LastOrDefault()?.Kind, sent.LastOrDefault()?.Payload ?? JsonSerializer.SerializeToElement(new { }));
        }

        public JsonElement FuelSummary(string participant)
        {
            var result = Send(participant, "GET_STATE", new { }).GetAwaiter().GetResult();
            return result.LastPayload.GetProperty("fuel");
        }

        public string RequestIdFrom(SendResult result)
        {
            var eventPayload = result.Events.FirstOrDefault(item => item.Kind == "REQUEST_QUEUED")?.Payload ?? result.LastPayload;
            return eventPayload.GetProperty("requestId").GetString()!;
        }

        public string RequestStatusFrom(SendResult result)
        {
            var response = Send("tanker", "GET_STATE", new { }).GetAwaiter().GetResult();
            return response.LastPayload.GetProperty("queue").GetProperty("pending")[0].GetProperty("status").GetString()!;
        }

        public JsonElement OperationViewFrom(SendResult result) => result.LastPayload;

        public void Reconnect(string participantId, long generation, string clientInstanceId)
        {
            var peer = _peers[participantId] with
            {
                IsConnected = true,
                ClientInstanceId = clientInstanceId,
                ConnectionGeneration = generation,
                Telemetry = new TacticalDisplay.Core.Models.TacticalTelemetry(1, _clock.GetUtcNow(), 60, 25, 10_000, 90, 90, 250)
            };
            _peers[participantId] = peer;
            _handler.OnParticipantConnected(peer);
        }

        public string OperationState(string operationId)
        {
            if (OperationForTests(operationId) is { } snapshot) return snapshot.State;
            var result = Send("tanker", "GET_STATE", new { }).GetAwaiter().GetResult();
            return result.LastPayload.GetProperty("operations").EnumerateArray()
                .Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("state").GetString()!;
        }

        public (string State, bool FuelOnAuthorized, double TransferredKg)? OperationForTests(string operationId) =>
            _handler.OperationForTests(operationId);

        public void RefreshPeerTelemetries()
        {
            foreach (var participant in _peers.Keys.ToArray())
                _peers[participant] = _peers[participant] with { Telemetry = FreshTelemetry() };
        }

        public void RefreshPeerTelemetriesAndClearPoses()
        {
            RefreshPeerTelemetries();
            foreach (var peer in _peers.Values) _handler.OnParticipantConnected(peer);
        }

        private TacticalDisplay.Core.Models.TacticalTelemetry FreshTelemetry() =>
            new(1, _clock.GetUtcNow(), 60, 25, 10_000, 90, 90, 250);

        private sealed class Registry(AarAircraftProfile tankerProfile, AarAircraftProfile receiverProfile) : IAarRegistryProvider
        {
            public AarRegistrySnapshot? Current { get; } = new(1, 1, DateTimeOffset.UtcNow, [tankerProfile, receiverProfile]);
            public bool IsAvailable => true;
            public string? Status => null;
            public event EventHandler? Changed { add { } remove { } }
        }
    }

    private sealed record SentEvent(string Recipient, string Kind, string? OperationId, long? Revision, string? CorrelationId, JsonElement Payload);
    private sealed record SendResult(IReadOnlyList<SentEvent> Events, string? LastKind, JsonElement LastPayload);
    private sealed record PendingTransferFixture(Rig Rig, ManualTimeProvider Clock, string OperationId, AarServerEvent Proposal,
        string ProposalId, double TargetKg, double DeltaKg);

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
