using System.Text.Json;
using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class TacticalLinkClientProtocolTests
{
    [Fact]
    public void ClientOutboundMessagesIncludeProtocolVersionOne()
    {
        using var document = JsonDocument.Parse(TacticalLinkClient.SerializeVersioned(new { type = "PING" }));
        Assert.Equal(1, document.RootElement.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public void UnsupportedServerProtocolIsRejectedAsPermanentDisconnect()
    {
        var client = new TacticalLinkClient(null!);
        client.HandleMessage("{\"protocolVersion\":99,\"type\":\"CONNECTED\"}");
        Assert.True(client.ProtocolRejected);
        Assert.Equal(TacticalLinkConnectionState.Disconnected, client.ConnectionState);
    }

    [Fact]
    public void RegisteredModuleReceivesOnlyItsOwnModuleEvents()
    {
        var client = new TacticalLinkClient(null!);
        var aar = new RecordingModule("aar", 1);
        client.RegisterModule(aar);

        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"presence\",\"moduleProtocolVersion\":1,\"transportSequence\":1,\"kind\":\"UPDATED\",\"payload\":{}}");
        Assert.Empty(aar.Events);

        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":2,\"kind\":\"AAR_STATE\",\"payload\":{\"queue\":{\"pending\":[]}}}");
        Assert.Single(aar.Events);
        Assert.Equal("AAR_STATE", aar.Events[0].Kind);
    }

    [Fact]
    public void DisposedAarClientUnregistersFromTransportEvents()
    {
        var client = new TacticalLinkClient(null!);
        var aar = new AarClient(client, CancellationToken.None);
        var received = 0;
        aar.EventReceived += (_, _) => received++;

        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":1,\"kind\":\"AAR_STATE\",\"payload\":{}}");
        aar.Dispose();
        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":2,\"kind\":\"QUEUE_UPDATED\",\"payload\":{}}");

        Assert.Equal(1, received);
    }

    [Fact]
    public void AarClientReconstructsTypedQueueAndOperationState()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);
        AarState? observed = null;
        aar.StateChanged += state => observed = state;

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"AAR_STATE","payload":{"queue":{"committedNext":{"operationId":"op-next","state":"Accepted","slot":"CommittedNext","operationRevision":4,"plannedKg":0,"transferredKg":0,"transferMode":"DryHookup"},"pending":[{"requestId":"req-1","receiverParticipantId":"receiver","source":"ReceiverRequest","requestMode":"Fixed","requestedKg":800,"queueOrder":1,"status":"Pending"}]},"operations":[{"operationId":"op-live","requestMode":"Full","source":"ReceiverRequest","state":"Astern","slot":"Active","operationRevision":9,"requestedKg":null,"plannedKg":0,"transferredKg":0,"transferMode":"DryHookup"}],"fuel":{"protectedReserveKg":500,"availableToPromiseKg":1000},"ownPendingRequestId":"req-own"}}
            """);

        Assert.NotNull(observed);
        Assert.Equal(AarOperationPhase.Astern, observed!.ActiveOperation!.Phase);
        Assert.Equal(9, observed.ActiveOperation.Revision);
        Assert.Equal(AarTransferMode.DryHookup, observed.TransferMode);
        Assert.Equal("op-next", observed.CommittedNext!.OperationId);
        Assert.Equal(800, Assert.Single(observed.PendingQueue).RequestedKg);
        Assert.Equal(500, observed.FuelSummary!.ProtectedReserveKg);
        Assert.Equal("req-own", observed.OwnPendingRequestId);
        Assert.Equal("Full", observed.ActiveOperation.RequestMode);
        Assert.Null(observed.ActiveOperation.RequestedKg);
    }

    [Fact]
    public void AarClientRejectsStaleOperationRevisionBeforeRaisingState()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);
        var updates = 0;
        aar.StateChanged += _ => updates++;

        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":1,\"kind\":\"OPERATION_STATE\",\"operationId\":\"op-1\",\"operationRevision\":21,\"payload\":{\"operationId\":\"op-1\",\"state\":\"Breakaway\",\"slot\":\"Active\",\"operationRevision\":21}}");
        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":2,\"kind\":\"TRANSFER_PROPOSAL\",\"operationId\":\"op-1\",\"operationRevision\":20,\"payload\":{\"operationId\":\"op-1\",\"state\":\"Refueling\",\"slot\":\"Active\",\"operationRevision\":20}}");

        Assert.Equal(1, updates);
        Assert.Equal(AarOperationPhase.Breakaway, aar.State.Operations["op-1"].Phase);
        Assert.Null(aar.State.ActiveOperation);
    }

    [Fact]
    public void AarClientUsesSnapshotRevisionAndReducesRequestAccepted()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"AAR_STATE","payload":{"operations":[{"operationId":"op-1","state":"Astern","slot":"Active","operationRevision":21,"plannedKg":250,"transferredKg":0,"transferMode":"Fuel"}]}}
            """);
        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":2,"kind":"TRANSFER_PROPOSAL","operationId":"op-1","operationRevision":20,"payload":{"operationId":"op-1","state":"Refueling","slot":"Active","operationRevision":20}}
            """);
        Assert.Equal(AarOperationPhase.Astern, aar.State.ActiveOperation!.Phase);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":3,"kind":"REQUEST_ACCEPTED","operationId":"op-2","operationRevision":1,"payload":{"operationId":"op-2","state":"Accepted","slot":"CommittedNext","plannedKg":100,"transferredKg":0,"transferMode":"Fuel"}}
            """);

        Assert.Equal(AarOperationPhase.Accepted, aar.State.Operations["op-2"].Phase);
        Assert.Equal(100, aar.State.Operations["op-2"].PlannedKg);
    }

    [Fact]
    public void AarClientUpdatesTransferredWatermarkFromConfirmedEvent()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"REFUELING_STARTED","operationId":"op-1","operationRevision":2,"payload":{"operationId":"op-1","state":"Refueling","slot":"Active","operationRevision":2,"plannedKg":100,"transferredKg":0,"remainingKg":100,"effectiveFlowKgPerSecond":10}}
            """);
        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":2,"kind":"TRANSFER_CONFIRMED","operationId":"op-1","operationRevision":3,"payload":{"operationId":"op-1","state":"Refueling","slot":"Active","operationRevision":3,"plannedKg":100,"transferredKg":25,"remainingKg":75,"effectiveFlowKgPerSecond":10}}
            """);

        Assert.Equal(25, aar.State.ActiveOperation!.TransferredKg);
        Assert.Equal(75, aar.State.ActiveOperation.RemainingKg);
    }

    [Fact]
    public void AarClientAppliesContactReleaseAsAsternState()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"REFUELING_STARTED","operationId":"op-1","operationRevision":2,"payload":{"operationId":"op-1","state":"Refueling","slot":"Active","operationRevision":2,"fuelOnAuthorized":true,"plannedKg":100,"transferredKg":10}}
            """);
        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":2,"kind":"CONTACT_RELEASED","operationId":"op-1","operationRevision":3,"payload":{"operationId":"op-1","state":"Astern","slot":"Active","operationRevision":3,"fuelOnAuthorized":false,"plannedKg":100,"transferredKg":10}}
            """);

        Assert.Equal(AarOperationPhase.Astern, aar.State.ActiveOperation!.Phase);
        Assert.False(aar.State.ActiveOperation.FuelOnAuthorized);
    }

    [Fact]
    public void AarClientReplacesQueueFromLiveQueueUpdatedEvent()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);
        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"AAR_STATE","payload":{"queue":{"pending":[{"requestId":"old","queueOrder":0,"status":"Pending"}]}}}
            """);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":2,"kind":"QUEUE_UPDATED","payload":{"pending":[{"requestId":"new","queueOrder":0,"status":"Pending"}],"committedNext":null}}
            """);

        Assert.Equal("new", Assert.Single(aar.State.PendingQueue).RequestId);
    }

    [Fact]
    public void AarClientTracksTankerAddedReceiverRequestForReceiverCancellation()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);
        client.HandleMessage("{\"protocolVersion\":1,\"type\":\"CONNECTED\",\"participantId\":\"local-receiver\"}");

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"TANKER_ADDED_RECEIVER","payload":{"requestId":"req-added","receiverParticipantId":"local-receiver","tankerParticipantId":"tanker","source":"TankerAdded","requestMode":"None","requestedKg":null,"status":"Pending"}}
            """);

        Assert.Equal("req-added", aar.State.OwnPendingRequestId);
    }

    [Fact]
    public void AarClientKeepsStructuredSafeMaximumForTheViewModel()
    {
        var client = new TacticalLinkClient(null!);
        using var aar = new AarClient(client, CancellationToken.None);

        client.HandleMessage("""
            {"protocolVersion":1,"type":"MODULE_EVENT","module":"aar","moduleProtocolVersion":1,"transportSequence":1,"kind":"MODULE_ERROR","payload":{"code":"PLANNED_AMOUNT_EXCEEDS_SAFE_MAXIMUM","message":"The plan exceeds safe limits.","maxAllowedKg":3200}}
            """);

        Assert.Equal("The plan exceeds safe limits.", aar.State.LastError);
        Assert.Equal(3200, aar.State.MaxAllowedKg);
    }

    [Fact]
    public void ConflictingModuleRegistrationIsRejected()
    {
        var client = new TacticalLinkClient(null!);
        client.RegisterModule(new RecordingModule("aar", 1));

        Assert.Throws<InvalidOperationException>(() => client.RegisterModule(new RecordingModule("aar", 1)));
    }

    private sealed class RecordingModule(string module, int protocolVersion) : ITacticalLinkClientModule
    {
        public string Module { get; } = module;
        public int ProtocolVersion { get; } = protocolVersion;
        public List<TacticalLinkModuleEvent> Events { get; } = [];
        public void HandleEvent(TacticalLinkModuleEvent message) => Events.Add(message);
    }
}
