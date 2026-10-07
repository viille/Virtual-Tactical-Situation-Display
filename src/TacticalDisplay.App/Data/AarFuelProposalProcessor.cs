namespace TacticalDisplay.App.Data;

public sealed record AarFuelProposalResult(
    double AppliedCumulativeKg,
    double AppliedKg,
    AarFuelApplyStatus Status,
    string? Error = null);

public sealed class AarFuelProposalProcessor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AarFuelProposalResult> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string OperationId, double DeltaKg, double TargetKg, string Role)> _identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _watermarks = new(StringComparer.Ordinal);

    public double GetWatermark(string operationId)
    {
        lock (_gate) return _watermarks.GetValueOrDefault(operationId);
    }

    public void RecordWatermark(string operationId, double cumulativeKg)
    {
        if (!double.IsFinite(cumulativeKg) || cumulativeKg < 0) return;
        lock (_gate) _watermarks[operationId] = Math.Max(_watermarks.GetValueOrDefault(operationId), cumulativeKg);
    }

    public async Task<AarFuelProposalResult?> ApplyAsync(
        string proposalId,
        string operationId,
        string role,
        double deltaKg,
        double targetCumulativeKg,
        double protectedReserveKg,
        IAarFuelAdapter? adapter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || string.IsNullOrWhiteSpace(operationId) ||
            role is not ("Tanker" or "Receiver") || !double.IsFinite(deltaKg) || deltaKg <= 0 ||
            !double.IsFinite(targetCumulativeKg) || targetCumulativeKg < deltaKg ||
            !double.IsFinite(protectedReserveKg) || protectedReserveKg < 0)
            throw new ArgumentException("The AAR fuel proposal is invalid.");

        var identity = (operationId, deltaKg, targetCumulativeKg, role);
        double startingWatermark;
        lock (_gate)
        {
            if (_identities.TryGetValue(proposalId, out var previousIdentity) && previousIdentity != identity)
                throw new InvalidOperationException("A proposal ID cannot be reused with a different operation or fuel delta.");
            _identities[proposalId] = identity;
            if (_completed.TryGetValue(proposalId, out var completed)) return completed;
            if (!_inFlight.Add(proposalId)) return null;
            startingWatermark = _watermarks.GetValueOrDefault(operationId);
            if (!_watermarks.ContainsKey(operationId))
                _watermarks[operationId] = startingWatermark = Math.Max(0, targetCumulativeKg - deltaKg);
        }

        AarFuelApplyResult application;
        try
        {
            if (adapter is null)
            {
                application = new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel adapter unavailable.");
            }
            else
            {
                var signedDelta = role == "Tanker" ? -deltaKg : deltaKg;
                var reserve = role == "Tanker" ? protectedReserveKg : 0;
                application = await new AarFuelApplicationService(adapter)
                    .ApplyDeltaKgAsync(signedDelta, reserve, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate) _inFlight.Remove(proposalId);
            throw;
        }
        catch (Exception ex)
        {
            application = new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, ex.Message);
        }

        var appliedKg = double.IsFinite(application.AppliedKg) ? Math.Abs(application.AppliedKg) : 0;
        var result = new AarFuelProposalResult(
            startingWatermark + appliedKg,
            appliedKg,
            application.Status,
            application.Error);
        lock (_gate)
        {
            _watermarks[operationId] = result.AppliedCumulativeKg;
            _completed[proposalId] = result;
            _inFlight.Remove(proposalId);
        }
        return result;
    }
}
