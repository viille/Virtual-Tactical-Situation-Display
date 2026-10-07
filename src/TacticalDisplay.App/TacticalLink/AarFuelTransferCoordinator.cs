using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.TacticalLink;

/// <summary>Applies server transfer proposals through the local simulator adapter and returns verified acknowledgements.</summary>
internal sealed class AarFuelTransferCoordinator : IDisposable
{
    private readonly AarClient _client;
    private readonly Func<IAarFuelAdapter?> _adapter;
    private readonly Func<double> _protectedReserveKg;
    private readonly CancellationToken _applicationToken;
    private readonly AarFuelProposalProcessor _processor = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private bool _disposed;

    public AarFuelTransferCoordinator(AarClient client, Func<IAarFuelAdapter?> adapter,
        Func<double> protectedReserveKg, CancellationToken applicationToken)
    {
        _client = client;
        _adapter = adapter;
        _protectedReserveKg = protectedReserveKg;
        _applicationToken = applicationToken;
        _client.EventReceived += OnEventReceived;
        _client.StateChanged += OnStateChanged;
        OnStateChanged(_client.State);
    }

    public double GetWatermark(string operationId) => _processor.GetWatermark(operationId);
    public void RecordWatermark(string operationId, double cumulativeKg) => _processor.RecordWatermark(operationId, cumulativeKg);

    private void OnStateChanged(AarState state)
    {
        foreach (var operation in state.Operations.Values)
            _processor.RecordWatermark(operation.OperationId, operation.TransferredKg);
    }

    private void OnEventReceived(object? sender, TacticalLinkModuleEvent message)
    {
        if (message.OperationId is { } operationId && message.OperationRevision is { } revision)
        {
            lock (_gate)
            {
                var previous = _revisions.GetValueOrDefault(operationId);
                if (revision < previous) return;
                if (revision > previous && _inFlight.Remove(operationId, out var pending))
                    pending.Cancel();
                _revisions[operationId] = revision;
            }
        }

        if (message.Kind == "TRANSFER_PROPOSAL") _ = ApplyProposalAsync(message);
    }

    private async Task ApplyProposalAsync(TacticalLinkModuleEvent message)
    {
        if (message.OperationId is not { } operationId || message.OperationRevision is not { } revision ||
            !message.Payload.TryGetProperty("proposalId", out var proposalNode) || proposalNode.GetString() is not { } proposalId ||
            !message.Payload.TryGetProperty("deltaKg", out var deltaNode) || !deltaNode.TryGetDouble(out var deltaKg) || deltaKg <= 0 ||
            !message.Payload.TryGetProperty("targetCumulativeKg", out var targetNode) || !targetNode.TryGetDouble(out var targetKg)) return;

        var role = message.Payload.TryGetProperty("participantRole", out var roleNode) ? roleNode.GetString() : null;
        if (role is not ("Tanker" or "Receiver")) return;

        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_disposed || _revisions.GetValueOrDefault(operationId) != revision) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
            if (_inFlight.Remove(operationId, out var previous)) previous.Dispose();
            _inFlight[operationId] = linked;
        }

        AarFuelProposalResult? application;
        try
        {
            application = await _processor.ApplyAsync(proposalId, operationId, role, deltaKg, targetKg,
                _protectedReserveKg(), _adapter(), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
        catch (InvalidOperationException ex)
        {
            DataSourceDebugLog.ThrottledDebug("AAR", "proposal-conflict", TimeSpan.FromSeconds(10), () => ex.Message);
            return;
        }
        finally
        {
            lock (_gate)
                if (_inFlight.TryGetValue(operationId, out var current) && ReferenceEquals(current, linked)) _inFlight.Remove(operationId);
            linked.Dispose();
        }
        if (application is null) return;

        lock (_gate)
            if (_disposed || _revisions.GetValueOrDefault(operationId) != revision) return;
        try
        {
            await _client.AcknowledgeTransferAsync(operationId, proposalId, application, revision, _applicationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationToken.IsCancellationRequested) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _inFlight.Values)
                pending.Cancel();
            _inFlight.Clear();
        }
        _client.EventReceived -= OnEventReceived;
        _client.StateChanged -= OnStateChanged;
    }
}
