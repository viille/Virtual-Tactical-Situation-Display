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
