namespace TacticalDisplay.App.Data;

public interface IAarBridgeTransport
{
    bool IsConnected { get; }
    Task<AarBridgeResponse> SendAsync(AarBridgeRequest request, CancellationToken cancellationToken);
}
