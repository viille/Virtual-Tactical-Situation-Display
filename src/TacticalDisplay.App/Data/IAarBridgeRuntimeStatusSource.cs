namespace TacticalDisplay.App.Data;

public interface IAarBridgeRuntimeStatusSource
{
    event EventHandler<AarBridgeRuntimeState>? BridgeRuntimeStateChanged;
    AarBridgeRuntimeState? BridgeRuntimeState { get; }
    string? BridgeVersion { get; }
    string? BridgeDiagnostic { get; }
}
