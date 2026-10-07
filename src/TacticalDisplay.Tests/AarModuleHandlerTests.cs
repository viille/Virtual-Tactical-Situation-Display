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

        var nextRequest = await rig.Request("receiver", "tanker", 150);
        var nextId = rig.RequestIdFrom(nextRequest);
        var nextOperation = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = nextId });
        Assert.Equal("CommittedNext", rig.OperationViewFrom(nextOperation).GetProperty("slot").GetString());
        Assert.Equal(250, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());

        var pending = await rig.Request("receiver", "tanker", 50);
        var thirdAcceptance = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(pending) });
        Assert.Equal("MODULE_ERROR", thirdAcceptance.LastKind);
        Assert.Equal(250, rig.FuelSummary("tanker").GetProperty("committedFuelKg").GetDouble());
        Assert.Equal("Pending", rig.RequestStatusFrom(pending));
        Assert.Equal(550, rig.FuelSummary("tanker").GetProperty("availableToPromiseKg").GetDouble());
    }

    [Fact]
    public async Task NetworkReconnectSuspendsThenRecoversOnlyToPreContact()
    {
        var rig = new Rig();
        await rig.PrepareTankerAndReceiver();
        var request = await rig.Request("receiver", "tanker", 100);
        var accepted = await rig.Send("tanker", "ACCEPT_REQUEST", new { requestId = rig.RequestIdFrom(request) });
        var operationId = accepted.LastPayload.GetProperty("operationId").GetString()!;
        await rig.Send("tanker", "PROCEED_TO_PRECONTACT", new { }, operationId: operationId);

        rig.Handler.OnParticipantDisconnected("user-r", "receiver", explicitDisconnect: false);
        Assert.Equal("Suspended", rig.OperationState(operationId));

        rig.Reconnect("receiver", 2, "instance-r");
        await rig.Send("tanker", "FUEL_STATUS", new { currentFuelKg = 1000, capacityKg = 2000, adapterReady = true, lastAppliedTransferredKg = 0 });
        await rig.Send("receiver", "FUEL_STATUS", new { currentFuelKg = 100, capacityKg = 500, adapterReady = true, lastAppliedTransferredKg = 0 });
        var reconciled = await rig.Send("receiver", "RECONCILE", new { operationId }, operationId: operationId);

        Assert.Equal("PreContact", reconciled.LastPayload.GetProperty("state").GetString());
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

    [Fact]
    public void UnknownReceiverLimitUsesMethodFallbackAgainstKnownTankerLimit()
    {
        var tanker = Rig.TankerProfile;
        var receiver = Rig.ReceiverProfile;

        Assert.Equal(10, AarModuleHandler.EffectiveBoomFlow(tanker, receiver));
    }

    private sealed class Rig
    {
        public static AarAircraftProfile TankerProfile { get; } = new("kc135r", "KC-135R/T", ["K35R"], true, true, false,
            [new("Boom", 45, "confirmed_aircraft_specific_value", ["source-1"])], [], [], null, "");
        public static AarAircraftProfile ReceiverProfile { get; } = new("f16", "F-16", ["F16"], true, false, true,
            [], [new("BoomReceptacle", null, "unknown", [])], [], null, "");
        private readonly AarModuleHandler _handler;
        private readonly ModuleRouter _router;
        private readonly Dictionary<string, AarPeerSnapshot> _peers = new(StringComparer.Ordinal)
        {
            ["tanker"] = new("tanker", "user-t", "1001", "TANKER", "K35R", true, "instance-t", 1, new HashSet<string>(["aar.tanker"]), new Dictionary<string, string>(), FreshTelemetry()),
            ["receiver"] = new("receiver", "user-r", "1002", "VIPER11", "F16", true, "instance-r", 1, new HashSet<string>(["aar.receiver"]), new Dictionary<string, string>(), FreshTelemetry())
        };
        private int _nextId;

        public Rig()
        {
            _handler = new AarModuleHandler(new Registry());
            _router = new ModuleRouter([_handler]);
        }

        public int RequestCount => _handler.RequestCountForTests;
        public AarModuleHandler Handler => _handler;

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
                Telemetry = new TacticalDisplay.Core.Models.TacticalTelemetry(1, DateTimeOffset.UtcNow, 60, 25, 10_000, 90, 90, 250)
            };
            _peers[participantId] = peer;
            _handler.OnParticipantConnected(peer);
        }

        public string OperationState(string operationId)
        {
            var result = Send("receiver", "GET_STATE", new { }).GetAwaiter().GetResult();
            return result.LastPayload.GetProperty("operations").EnumerateArray()
                .Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("state").GetString()!;
        }

        private static TacticalDisplay.Core.Models.TacticalTelemetry FreshTelemetry() =>
            new(1, DateTimeOffset.UtcNow, 60, 25, 10_000, 90, 90, 250);

        private sealed class Registry : IAarRegistryProvider
        {
            public AarRegistrySnapshot? Current { get; } = new(1, 1, DateTimeOffset.UtcNow, [TankerProfile, ReceiverProfile]);
            public bool IsAvailable => true;
            public string? Status => null;
            public event EventHandler? Changed { add { } remove { } }
        }
    }

    private sealed record SentEvent(string Recipient, string Kind, string? OperationId, long? Revision, string? CorrelationId, JsonElement Payload);
    private sealed record SendResult(IReadOnlyList<SentEvent> Events, string? LastKind, JsonElement LastPayload);
}
