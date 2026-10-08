using System.Text.Json;
using TacticalLink.Server.Modules;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class ModuleRouterTests
{
    [Fact]
    public async Task KnownModuleReceivesValidatedCommandAndCanReply()
    {
        using var document = JsonDocument.Parse("""
            { "type":"MODULE_MESSAGE", "module":"aar", "moduleProtocolVersion":1, "messageId":"cmd-1", "kind":"PING", "operationId":null, "payload":{} }
            """);
        var events = new List<(string Kind, object Payload)>();
        var handler = new TestHandler();
        var router = new ModuleRouter([handler]);
        var connection = CreateConnection(events);

        await router.RouteAsync(document.RootElement, connection, CancellationToken.None);

        Assert.Equal("PING", handler.ReceivedKind);
        Assert.Collection(events, item => Assert.Equal("PONG", item.Kind));
    }

    [Fact]
    public async Task UnknownModuleReturnsModuleScopedErrorWithoutClosingConnection()
    {
        using var document = JsonDocument.Parse("""
            { "type":"MODULE_MESSAGE", "module":"formation", "moduleProtocolVersion":1, "messageId":"cmd-2", "kind":"JOIN", "payload":{} }
            """);
        var events = new List<(string Kind, object Payload)>();
        var router = new ModuleRouter([]);

        await router.RouteAsync(document.RootElement, CreateConnection(events), CancellationToken.None);

        Assert.Collection(events, item =>
        {
            Assert.Equal("MODULE_ERROR", item.Kind);
            Assert.Contains("MODULE_NOT_SUPPORTED", JsonSerializer.Serialize(item.Payload));
        });
    }

    [Theory]
    [InlineData("payload", "[]")]
    [InlineData("kind", "\"lowercase\"")]
    [InlineData("messageId", "\"bad id\"")]
    public async Task InvalidEnvelopeIsRejectedBeforeHandler(string field, string value)
    {
        var json = "{\"module\":\"aar\",\"moduleProtocolVersion\":1,\"messageId\":\"cmd-3\",\"kind\":\"PING\",\"payload\":{}}";
        using var source = JsonDocument.Parse(json);
        var command = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.RootElement.GetRawText())!;
        using var replacement = JsonDocument.Parse(value);
        command[field] = replacement.RootElement.Clone();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(command));
        var handler = new TestHandler();
        var events = new List<(string Kind, object Payload)>();
        var router = new ModuleRouter([handler]);

        await router.RouteAsync(document.RootElement, CreateConnection(events), CancellationToken.None);

        Assert.Null(handler.ReceivedKind);
        Assert.Single(events);
        Assert.Equal("MODULE_ERROR", events[0].Kind);
    }

    private static ModuleConnectionContext CreateConnection(List<(string Kind, object Payload)> events) => new(
        "participant-1", 2, () => true,
        (_, _, _, kind, _, _, _, payload) => { events.Add((kind, payload)); return true; },
        (_, _) => { });

    private sealed class TestHandler : IModuleMessageHandler
    {
        public string Module => "aar";
        public int ProtocolVersion => 1;
        public string? ReceivedKind { get; private set; }
        public Task HandleAsync(ModuleCommandContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedKind = context.Kind;
            context.Reply("PONG", null, null, new { messageId = context.MessageId });
            return Task.CompletedTask;
        }
    }
}
