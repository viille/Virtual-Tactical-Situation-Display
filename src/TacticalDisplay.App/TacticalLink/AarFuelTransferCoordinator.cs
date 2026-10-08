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
    private const int MaxRememberedOperations = 512;
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
        {
            _processor.RecordWatermark(operation.OperationId, operation.TransferredKg);
            DataSourceDebugLog.ThrottledDebug("AAR", $"watermark:{operation.OperationId}", TimeSpan.FromSeconds(5), () =>
                $"Operation fuel watermark | operationId={operation.OperationId} lastAppliedTransferredKg={_processor.GetWatermark(operation.OperationId):0.00}");
        }
        HashSet<string> retained = state.Operations.Keys.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            retained.UnionWith(_inFlight.Keys);
            var live = state.Operations.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var operationId in _revisions.Keys.Where(id => !live.Contains(id) && !_inFlight.ContainsKey(id))
                         .Take(Math.Max(0, _revisions.Count - MaxRememberedOperations)).ToArray())
                _revisions.Remove(operationId);
        }
        _processor.RetainOperations(retained);
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
                {
                    DataSourceDebugLog.Debug("AAR", $"In-flight proposal superseded | operationId={operationId} proposalRevision={previous} currentRevision={revision} reason={message.Kind} mutationMayHaveStarted=True");
                    pending.Cancel();
                }
                _revisions[operationId] = revision;
                if (_revisions.Count > MaxRememberedOperations)
                    foreach (var expired in _revisions.Keys.Where(id => !_inFlight.ContainsKey(id)).Take(_revisions.Count - MaxRememberedOperations).ToArray())
                        _revisions.Remove(expired);
            }
        }

        if (message.Kind == "TRANSFER_ACCOUNTING_SETTLED")
            DataSourceDebugLog.Debug("AAR", $"Superseded proposal settled | operationId={message.OperationId} revision={message.OperationRevision} cumulativeKg={(message.Payload.TryGetProperty("transferredKg", out var transferred) && transferred.TryGetDouble(out var value) ? value : 0):0.00}");
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
        DataSourceDebugLog.Debug("AAR", $"Transfer proposal | operationId={operationId} revision={revision} proposalId={proposalId} targetCumulativeKg={targetKg:0.00} localDeltaKg={deltaKg:0.00}");

        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_disposed || _revisions.GetValueOrDefault(operationId) != revision) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
            if (_inFlight.Remove(operationId, out var previous)) previous.Dispose();
            _inFlight[operationId] = linked;
        }

        AarFuelProposalResult? application;
        var applicationTask = _processor.ApplyAsync(proposalId, operationId, role, deltaKg, targetKg,
            _protectedReserveKg(), _adapter(), linked.Token);
        try
        {
            application = await applicationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A cancellation may race with a physical write and its read-back. Keep
            // waiting without cancellation so a verified result can be settled.
            try
            {
                application = await applicationTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Error("AAR", $"Superseded proposal outcome is unknown | operationId={operationId} proposalId={proposalId}", ex);
                return;
            }
        }
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

        DataSourceDebugLog.Debug("AAR", $"Transfer applied | operationId={operationId} revision={revision} proposalId={proposalId} appliedKg={application.AppliedKg:0.00} localCumulativeKg={application.AppliedCumulativeKg:0.00} status={application.Status}");

        lock (_gate)
            if (_disposed) return;
        try
        {
            await _client.AcknowledgeTransferAsync(operationId, proposalId, targetKg, application, revision, _applicationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationToken.IsCancellationRequested) { }
        catch (InvalidOperationException ex)
        {
            DataSourceDebugLog.Debug("AAR", $"Transfer settlement rejected | operationId={operationId} revision={revision} proposalId={proposalId} appliedKg={application.AppliedKg:0.00} reason={ex.Message}");
        }
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
