using System.Text.Json;
using TacticalDisplay.Core.Models;

namespace TacticalLink.Server.Modules;

public sealed record ModuleCommand(
    string Module,
    int ModuleProtocolVersion,
    string MessageId,
    string Kind,
    string? OperationId,
    JsonElement Payload);

public sealed record AarPeerSnapshot(
    string ParticipantId,
    string UserId,
    string VatsimCid,
    string Callsign,
    string? AircraftType,
    bool IsConnected,
    string? ClientInstanceId,
    long ConnectionGeneration,
    IReadOnlySet<string> Capabilities,
    IReadOnlyDictionary<string, string> OperationalStates,
    TacticalTelemetry? Telemetry);

public sealed class ModuleConnectionContext(
    string participantId,
    long connectionGeneration,
    Func<bool> isCurrentGeneration,
    Func<string, string, int, string, string?, long?, string?, object, bool> sendEvent,
    Action<string, string> setOperationalState,
    AarPeerSnapshot? currentPeer = null,
    Func<IReadOnlyCollection<AarPeerSnapshot>>? getPeers = null)
{
    public string ParticipantId { get; } = participantId;
    public long ConnectionGeneration { get; } = connectionGeneration;
    public AarPeerSnapshot? CurrentPeer { get; } = currentPeer;
    public IReadOnlyCollection<AarPeerSnapshot> GetPeers() => getPeers?.Invoke() ?? (CurrentPeer is null ? [] : [CurrentPeer]);
    public bool IsCurrentGeneration => isCurrentGeneration();
    public bool SendEvent(string recipientParticipantId, string module, int moduleProtocolVersion, string kind, string? operationId, long? operationRevision, object payload, string? messageId = null) =>
        IsCurrentGeneration && sendEvent(recipientParticipantId, module, moduleProtocolVersion, kind, operationId, operationRevision, messageId, payload);
    public void SetOperationalState(string name, string value)
    {
        if (IsCurrentGeneration) setOperationalState(name, value);
    }
}

public sealed class ModuleCommandContext(ModuleConnectionContext connection, ModuleCommand command)
{
    public string ParticipantId => connection.ParticipantId;
    public long ConnectionGeneration => connection.ConnectionGeneration;
    public string Module => command.Module;
    public int ModuleProtocolVersion => command.ModuleProtocolVersion;
    public string MessageId => command.MessageId;
    public string Kind => command.Kind;
    public string? OperationId => command.OperationId;
    public JsonElement Payload => command.Payload;
    public bool IsCurrentGeneration => connection.IsCurrentGeneration;
    public AarPeerSnapshot? CurrentPeer => connection.CurrentPeer;
    public IReadOnlyCollection<AarPeerSnapshot> GetPeers() => connection.GetPeers();

    public bool SendEvent(string recipientParticipantId, string kind, string? operationId, long? operationRevision, object payload) =>
        connection.SendEvent(recipientParticipantId, Module, ModuleProtocolVersion, kind, operationId, operationRevision, payload);

    public bool Reply(string kind, string? operationId, long? operationRevision, object payload) =>
        connection.SendEvent(ParticipantId, Module, ModuleProtocolVersion, kind, operationId, operationRevision, payload, MessageId);

    public void SetOperationalState(string name, string value) => connection.SetOperationalState(name, value);
}

public interface IModuleMessageHandler
{
    string Module { get; }
    int ProtocolVersion { get; }
    Task HandleAsync(ModuleCommandContext context, CancellationToken cancellationToken);
}

public sealed class ModuleRouter
{
    private readonly IReadOnlyDictionary<string, IModuleMessageHandler> _handlers;

    public ModuleRouter(IEnumerable<IModuleMessageHandler> handlers)
    {
        var registered = handlers.ToArray();
        if (registered.Any(handler => string.IsNullOrWhiteSpace(handler.Module) || handler.ProtocolVersion < 1))
            throw new InvalidOperationException("Module handlers require a name and positive protocol version.");
        _handlers = registered.ToDictionary(handler => handler.Module, StringComparer.Ordinal);
    }

    public async Task RouteAsync(JsonElement envelope, ModuleConnectionContext connection, CancellationToken cancellationToken)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("module", out var moduleNode) || moduleNode.ValueKind != JsonValueKind.String)
        {
            SendProtocolError(connection, "", 1, "", "INVALID_MODULE_ENVELOPE", "A module name is required.");
            return;
        }
        var module = moduleNode.GetString() ?? "";
        if (module.Length is < 1 or > 32 || module.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            SendProtocolError(connection, "", 1, "", "INVALID_MODULE_ENVELOPE", "The module name is invalid.");
            return;
        }
        if (!envelope.TryGetProperty("moduleProtocolVersion", out var versionNode) || !versionNode.TryGetInt32(out var version) || version < 1 || version > 255 ||
            !envelope.TryGetProperty("messageId", out var idNode) || idNode.ValueKind != JsonValueKind.String ||
            !envelope.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String ||
            !envelope.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            SendProtocolError(connection, module, 1, "", "INVALID_MODULE_ENVELOPE", "Module version, message ID, kind and object payload are required.");
            return;
        }
        var messageId = idNode.GetString() ?? "";
        var kind = kindNode.GetString() ?? "";
        if (messageId.Length is < 1 or > 80 || !IsSafeId(messageId) || kind.Length is < 1 or > 64 || kind[0] is < 'A' or > 'Z' || kind.Any(character => character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_'))
        {
            SendProtocolError(connection, module, version, messageId, "INVALID_MODULE_ENVELOPE", "The module message ID or kind is invalid.");
            return;
        }
        string? operationId = null;
        if (envelope.TryGetProperty("operationId", out var operationNode) && operationNode.ValueKind != JsonValueKind.Null)
        {
            var candidate = operationNode.ValueKind == JsonValueKind.String ? operationNode.GetString() : null;
            if (candidate is null || candidate.Length is < 1 or > 80 || !IsSafeId(candidate))
            {
                SendProtocolError(connection, module, version, messageId, "INVALID_MODULE_ENVELOPE", "The operation ID is invalid.");
                return;
            }
            operationId = candidate;
        }
        if (!_handlers.TryGetValue(module, out var handler))
        {
            SendProtocolError(connection, module, version, messageId, "MODULE_NOT_SUPPORTED", "This TacticalLink server does not support the requested module.");
            return;
        }
        if (handler.ProtocolVersion != version)
        {
            SendProtocolError(connection, module, version, messageId, "MODULE_VERSION_NOT_SUPPORTED", "This module protocol version is not supported.");
            return;
        }
        if (!connection.IsCurrentGeneration) return;
        try
        {
            await handler.HandleAsync(new ModuleCommandContext(connection, new ModuleCommand(module, version, messageId, kind, operationId, payload.Clone())), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            SendProtocolError(connection, module, version, messageId, "MODULE_COMMAND_FAILED", "The module could not complete this command.");
        }
    }

    private static bool IsSafeId(string value) => value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void SendProtocolError(ModuleConnectionContext connection, string module, int version, string messageId, string code, string message) =>
        connection.SendEvent(connection.ParticipantId, module, version, "MODULE_ERROR", null, null, new { messageId, code, message }, messageId);
}
