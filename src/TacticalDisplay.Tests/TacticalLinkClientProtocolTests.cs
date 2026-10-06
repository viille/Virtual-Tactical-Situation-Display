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
}
